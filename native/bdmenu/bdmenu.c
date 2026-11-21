/*
 * bdmenu - plays a decrypted Blu-ray (folder or ISO) with HDMV and BD-J menus through libbluray,
 * draws the menu graphics over the video and writes a live H.264/AAC MPEG-TS stream to stdout.
 *
 * Used by the Jellyfin "Disc Menus" plugin. One process per menu session.
 *
 *   bdmenu --probe --disc PATH                    JSON disc info on stdout
 *   bdmenu --menu  --disc PATH [options]          live MPEG-TS on stdout
 *
 * Options: --vbitrate KBPS --preset NAME --encoder NAME --region A|B|C --lang ISO639-2
 *          --persist DIR --cache DIR --auto-enter SECONDS --no-bdj --fps NUM/DEN
 *
 * stdin (one command per line):
 *   key up|down|left|right|enter|menu|popup|back|0-9|red|green|yellow|blue|play|pause|stop|next|prev
 *   click X Y | move X Y      (0..1 across the picture)
 *   title N | audio N | pg N (0 = off) | chapter +1|-1 | quit
 *
 * stderr: log lines, plus "@evt {json}" lines with disc state changes.
 *
 * No AACS/BD+ code: encrypted discs are reported and refused.
 */

#include <errno.h>
#include <inttypes.h>
#include <math.h>
#include <pthread.h>
#include <signal.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#endif

#include <libbluray/bluray.h>
#include <libbluray/keys.h>
#include <libbluray/overlay.h>

#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/audio_fifo.h>
#include <libavutil/channel_layout.h>
#include <libavutil/imgutils.h>
#include <libavutil/opt.h>
#include <libavutil/time.h>
#include <libswresample/swresample.h>
#include <libswscale/swscale.h>

#define OUT_W 1920
#define OUT_H 1080
#define OUT_RATE 48000
#define READ_CHUNK (6144 * 8)
#define VQ_MAX 12

/* ------------------------------------------------------------------ options */

static struct {
    const char *disc;
    int probe;
    int vbitrate_kbps;
    const char *preset;
    const char *encoder;
    int region;
    const char *lang;
    const char *persist;
    const char *cache;
    int auto_enter;
    int no_bdj;
    AVRational fps;
} opt = { NULL, 0, 10000, "veryfast", "auto", 1, "eng", NULL, NULL, 0, 0, { 24000, 1001 } };

/* ------------------------------------------------------------------ events */

static pthread_mutex_t log_lock = PTHREAD_MUTEX_INITIALIZER;

static void emit(const char *fmt, ...)
{
    va_list ap;
    pthread_mutex_lock(&log_lock);
    fputs("@evt {", stderr);
    va_start(ap, fmt);
    vfprintf(stderr, fmt, ap);
    va_end(ap);
    fputs("}\n", stderr);
    fflush(stderr);
    pthread_mutex_unlock(&log_lock);
}

static void logmsg(const char *fmt, ...)
{
    va_list ap;
    pthread_mutex_lock(&log_lock);
    va_start(ap, fmt);
    vfprintf(stderr, fmt, ap);
    va_end(ap);
    fputc('\n', stderr);
    fflush(stderr);
    pthread_mutex_unlock(&log_lock);
}

static double now_s(void) { return av_gettime_relative() / 1e6; }

/* ------------------------------------------------------------------ overlay planes */

typedef struct {
    int w, h;
    uint32_t *back;  /* being drawn */
    uint32_t *front; /* shown */
    int has_content; /* front has any non-transparent pixel */
} Plane;

static Plane planes[2]; /* 0 = PG (subtitles), 1 = IG / BD-J graphics */
static uint32_t palette[2][256];
static pthread_mutex_t plane_lock = PTHREAD_MUTEX_INITIALIZER;

static void plane_free(Plane *p)
{
    av_freep(&p->back);
    av_freep(&p->front);
    p->w = p->h = 0;
    p->has_content = 0;
}

static void plane_init(Plane *p, int w, int h)
{
    if (w <= 0 || h <= 0 || w > 4096 || h > 4096) {
        return;
    }
    if (p->w == w && p->h == h && p->back) {
        memset(p->back, 0, (size_t)w * h * 4);
        memset(p->front, 0, (size_t)w * h * 4);
        p->has_content = 0;
        return;
    }
    plane_free(p);
    p->back = av_mallocz((size_t)w * h * 4);
    p->front = av_mallocz((size_t)w * h * 4);
    if (!p->back || !p->front) {
        plane_free(p);
        return;
    }
    p->w = w;
    p->h = h;
}

static void plane_flush(Plane *p)
{
    size_t n = (size_t)p->w * p->h;
    if (!p->back) {
        return;
    }
    memcpy(p->front, p->back, n * 4);
    p->has_content = 0;
    for (size_t i = 0; i < n; i++) {
        if (p->front[i] >> 24) {
            p->has_content = 1;
            break;
        }
    }
}

static void plane_clear_rect(Plane *p, int x, int y, int w, int h)
{
    if (!p->back) {
        return;
    }
    for (int j = y; j < y + h && j < p->h; j++) {
        if (j < 0 || x >= p->w) {
            continue;
        }
        int ww = (x + w > p->w) ? p->w - x : w;
        if (ww > 0) {
            memset(p->back + (size_t)j * p->w + x, 0, (size_t)ww * 4);
        }
    }
}

static uint32_t ycrcb_to_argb(uint8_t y, uint8_t cr, uint8_t cb, uint8_t a)
{
    /* BT.709, limited range */
    double Y = 1.164383 * (y - 16), Cb = cb - 128.0, Cr = cr - 128.0;
    int r = (int)lrint(Y + 1.792741 * Cr);
    int g = (int)lrint(Y - 0.213249 * Cb - 0.532909 * Cr);
    int b = (int)lrint(Y + 2.112402 * Cb);
    r = r < 0 ? 0 : r > 255 ? 255 : r;
    g = g < 0 ? 0 : g > 255 ? 255 : g;
    b = b < 0 ? 0 : b > 255 ? 255 : b;
    return ((uint32_t)a << 24) | ((uint32_t)r << 16) | ((uint32_t)g << 8) | (uint32_t)b;
}

/* HDMV (palette + RLE) overlays: menus (IG) and subtitles (PG). */
static void overlay_cb(void *handle, const BD_OVERLAY *ov)
{
    (void)handle;
    pthread_mutex_lock(&plane_lock);
    if (!ov) {
        plane_free(&planes[0]);
        plane_free(&planes[1]);
        pthread_mutex_unlock(&plane_lock);
        return;
    }
    int idx = ov->plane & 1;
    Plane *p = &planes[idx];
    switch (ov->cmd) {
    case BD_OVERLAY_INIT:
        plane_init(p, ov->w, ov->h);
        break;
    case BD_OVERLAY_CLOSE:
        plane_free(p);
        break;
    case BD_OVERLAY_CLEAR:
        if (p->back) {
            memset(p->back, 0, (size_t)p->w * p->h * 4);
        }
        break;
    case BD_OVERLAY_WIPE:
        plane_clear_rect(p, ov->x, ov->y, ov->w, ov->h);
        break;
    case BD_OVERLAY_HIDE:
        if (p->back) {
            memset(p->back, 0, (size_t)p->w * p->h * 4);
            plane_flush(p);
        }
        break;
    case BD_OVERLAY_DRAW:
        if (ov->palette) {
            for (int i = 0; i < 256; i++) {
                const BD_PG_PALETTE_ENTRY *e = &ov->palette[i];
                palette[idx][i] = ycrcb_to_argb(e->Y, e->Cr, e->Cb, e->T);
            }
        }
        if (p->back && ov->img) {
            const BD_PG_RLE_ELEM *rle = ov->img;
            for (int y = 0; y < ov->h; y++) {
                int x = 0;
                while (x < ov->w) {
                    int len = rle->len;
                    uint32_t c = palette[idx][rle->color & 0xff];
                    rle++;
                    if (len == 0) {
                        break; /* end of line */
                    }
                    int dy = ov->y + y;
                    for (int k = 0; k < len && x < ov->w; k++, x++) {
                        int dx = ov->x + x;
                        if (dx >= 0 && dx < p->w && dy >= 0 && dy < p->h) {
                            p->back[(size_t)dy * p->w + dx] = c;
                        }
                    }
                }
                if (x >= ov->w && rle->len == 0) {
                    rle++; /* consume end-of-line marker */
                }
            }
        }
        break;
    case BD_OVERLAY_FLUSH:
        plane_flush(p);
        break;
    default:
        break;
    }
    pthread_mutex_unlock(&plane_lock);
}

/* ARGB overlays: BD-J graphics (and HDMV when libbluray renders to ARGB). */
static void argb_overlay_cb(void *handle, const BD_ARGB_OVERLAY *ov)
{
    (void)handle;
    pthread_mutex_lock(&plane_lock);
    if (!ov) {
        plane_free(&planes[1]);
        pthread_mutex_unlock(&plane_lock);
        return;
    }
    Plane *p = &planes[ov->plane & 1];
    switch (ov->cmd) {
    case BD_ARGB_OVERLAY_INIT:
        plane_init(p, ov->w, ov->h);
        break;
    case BD_ARGB_OVERLAY_CLOSE:
        plane_free(p);
        break;
    case BD_ARGB_OVERLAY_DRAW:
        if (p->back && ov->argb) {
            for (int y = 0; y < ov->h; y++) {
                int dy = ov->y + y;
                if (dy < 0 || dy >= p->h) {
                    continue;
                }
                int w = ov->w;
                if (ov->x + w > p->w) {
                    w = p->w - ov->x;
                }
                if (w > 0 && ov->x >= 0) {
                    memcpy(p->back + (size_t)dy * p->w + ov->x, ov->argb + (size_t)y * ov->stride, (size_t)w * 4);
                }
            }
        }
        break;
    case BD_ARGB_OVERLAY_FLUSH:
        plane_flush(p);
        break;
    default:
        break;
    }
    pthread_mutex_unlock(&plane_lock);
}

/* Alpha-blends a plane (any size, scaled by nearest neighbour) onto the BGRA canvas. */
static void blend_plane(uint8_t *canvas, int stride, const Plane *p)
{
    if (!p->front || !p->has_content) {
        return;
    }
    for (int y = 0; y < OUT_H; y++) {
        int sy = (int)((int64_t)y * p->h / OUT_H);
        const uint32_t *src = p->front + (size_t)sy * p->w;
        uint8_t *dst = canvas + (size_t)y * stride;
        for (int x = 0; x < OUT_W; x++) {
            uint32_t c = src[(int64_t)x * p->w / OUT_W];
            uint32_t a = c >> 24;
            if (!a) {
                continue;
            }
            uint8_t *d = dst + x * 4; /* B G R A */
            if (a == 255) {
                d[0] = c & 0xff;
                d[1] = (c >> 8) & 0xff;
                d[2] = (c >> 16) & 0xff;
            } else {
                uint32_t ia = 255 - a;
                d[0] = (uint8_t)(((c & 0xff) * a + d[0] * ia) / 255);
                d[1] = (uint8_t)((((c >> 8) & 0xff) * a + d[1] * ia) / 255);
                d[2] = (uint8_t)((((c >> 16) & 0xff) * a + d[2] * ia) / 255);
            }
            d[3] = 255;
        }
    }
}

/* ------------------------------------------------------------------ commands from stdin */

#define CMDQ 64
static char cmdq[CMDQ][128];
static int cmdq_head, cmdq_tail;
static pthread_mutex_t cmd_lock = PTHREAD_MUTEX_INITIALIZER;
static volatile int quit_flag;

static void *stdin_thread(void *arg)
{
    (void)arg;
    char line[256];
    while (fgets(line, sizeof line, stdin)) {
        line[strcspn(line, "\r\n")] = 0;
        pthread_mutex_lock(&cmd_lock);
        int next = (cmdq_tail + 1) % CMDQ;
        if (next != cmdq_head) {
            snprintf(cmdq[cmdq_tail], sizeof cmdq[0], "%s", line);
            cmdq_tail = next;
        }
        pthread_mutex_unlock(&cmd_lock);
    }
    quit_flag = 1; /* stdin closed: the plugin went away */
    return NULL;
}

static int pop_command(char *out, size_t n)
{
    int ok = 0;
    pthread_mutex_lock(&cmd_lock);
    if (cmdq_head != cmdq_tail) {
        snprintf(out, n, "%s", cmdq[cmdq_head]);
        cmdq_head = (cmdq_head + 1) % CMDQ;
        ok = 1;
    }
    pthread_mutex_unlock(&cmd_lock);
    return ok;
}

/* ------------------------------------------------------------------ player state */

typedef struct {
    uint8_t *buf;
    int len, cap;
    int active;
} Pes;

typedef struct {
    AVFrame *frame;
    double show;
    int64_t pts;
} QueuedFrame;

static struct {
    BLURAY *bd;
    BLURAY_TITLE_INFO *ti;
    int playitem;
    int audio_number; /* 1-based */
    int pg_number;
    int pg_enabled;
    int title;
    int chapter;
    int in_menu, popup, still, paused;
    double still_until; /* 0 = no timed still */
    double next_read;

    int video_pid, video_coding;
    int audio_pid, audio_coding;

    Pes vpes, apes;
    AVCodecContext *vdec, *adec;
    AVCodecParserContext *vparser, *aparser;
    struct SwrContext *swr;
    AVChannelLayout swr_in_layout;
    int swr_in_rate, swr_in_fmt;

    int resync;
    int64_t base_pts;
    double base_clock;
    double last_show;

    QueuedFrame vq[VQ_MAX];
    int vq_count;
    AVFrame *shown;
    int64_t shown_pts;

    AVAudioFifo *afifo; /* decoded, resampled audio waiting to be shown */
    double afifo_start; /* output-clock time of the first sample in afifo */

    double auto_enter_at;
    int user_input_seen;
} P;

/* ------------------------------------------------------------------ output (encoder + muxer) */

static struct {
    AVFormatContext *oc;
    AVCodecContext *venc, *aenc;
    AVStream *vst, *ast;
    struct SwsContext *to_canvas, *to_enc;
    int to_canvas_w, to_canvas_h, to_canvas_fmt;
    uint8_t *canvas;
    int canvas_stride;
    AVFrame *encframe;
    AVFrame *aframe;
    AVAudioFifo *aout; /* samples ready for the AAC encoder */
    int64_t vframes;
    int64_t asamples;
} O;

static enum AVPixelFormat pick_pix_fmt(const AVCodec *c)
{
    const enum AVPixelFormat *fmts = NULL;
#if LIBAVCODEC_VERSION_INT >= AV_VERSION_INT(61, 13, 100)
    avcodec_get_supported_config(NULL, c, AV_CODEC_CONFIG_PIX_FORMAT, 0, (const void **)&fmts, NULL);
#else
    fmts = c->pix_fmts;
#endif
    if (!fmts) {
        return AV_PIX_FMT_YUV420P;
    }
    for (const enum AVPixelFormat *f = fmts; *f != AV_PIX_FMT_NONE; f++) {
        if (*f == AV_PIX_FMT_YUV420P) {
            return *f;
        }
    }
    for (const enum AVPixelFormat *f = fmts; *f != AV_PIX_FMT_NONE; f++) {
        if (*f == AV_PIX_FMT_NV12) {
            return *f;
        }
    }
    return AV_PIX_FMT_NONE;
}

static AVCodecContext *open_video_encoder(void)
{
    const char *order[] = { "libx264", "h264_nvenc", "h264_qsv", "h264_amf", "h264_videotoolbox", "h264_mf", "libopenh264", NULL };
    const char *tries[16];
    int n = 0;
    if (opt.encoder && strcmp(opt.encoder, "auto") != 0) {
        tries[n++] = opt.encoder;
    }
    for (int i = 0; order[i]; i++) {
        tries[n++] = order[i];
    }
    tries[n] = NULL;

    for (int i = 0; tries[i]; i++) {
        const AVCodec *c = avcodec_find_encoder_by_name(tries[i]);
        if (!c) {
            continue;
        }
        enum AVPixelFormat pf = pick_pix_fmt(c);
        if (pf == AV_PIX_FMT_NONE) {
            continue;
        }
        AVCodecContext *ctx = avcodec_alloc_context3(c);
        ctx->width = OUT_W;
        ctx->height = OUT_H;
        ctx->pix_fmt = pf;
        ctx->time_base = av_inv_q(opt.fps);
        ctx->framerate = opt.fps;
        ctx->sample_aspect_ratio = (AVRational){ 1, 1 };
        ctx->gop_size = (int)lrint(av_q2d(opt.fps)); /* 1 s GOP for quick joins and seeks */
        ctx->max_b_frames = 0;
        ctx->bit_rate = (int64_t)opt.vbitrate_kbps * 1000;
        ctx->rc_max_rate = ctx->bit_rate * 3 / 2;
        ctx->rc_buffer_size = (int)ctx->bit_rate;
        ctx->color_primaries = AVCOL_PRI_BT709;
        ctx->color_trc = AVCOL_TRC_BT709;
        ctx->colorspace = AVCOL_SPC_BT709;
        ctx->color_range = AVCOL_RANGE_MPEG;
        if (O.oc->oformat->flags & AVFMT_GLOBALHEADER) {
            ctx->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
        }
        if (!strcmp(c->name, "libx264")) {
            av_opt_set(ctx->priv_data, "preset", opt.preset, 0);
            av_opt_set(ctx->priv_data, "tune", "zerolatency", 0);
            av_opt_set(ctx->priv_data, "profile", "high", 0);
        } else if (!strcmp(c->name, "h264_nvenc")) {
            av_opt_set(ctx->priv_data, "preset", "p3", 0);
            av_opt_set(ctx->priv_data, "tune", "ll", 0);
        } else if (!strcmp(c->name, "h264_videotoolbox")) {
            av_opt_set_int(ctx->priv_data, "realtime", 1, 0);
        }
        if (avcodec_open2(ctx, c, NULL) == 0) {
            logmsg("bdmenu: video encoder %s", c->name);
            return ctx;
        }
        logmsg("bdmenu: encoder %s unavailable, trying the next one", c->name);
        avcodec_free_context(&ctx);
    }
    return NULL;
}

static int open_output(void)
{
    if (avformat_alloc_output_context2(&O.oc, NULL, "mpegts", NULL) < 0) {
        return -1;
    }
    O.venc = open_video_encoder();
    if (!O.venc) {
        logmsg("bdmenu: no usable H.264 encoder");
        return -1;
    }

    const AVCodec *ac = avcodec_find_encoder(AV_CODEC_ID_AAC);
    O.aenc = avcodec_alloc_context3(ac);
    O.aenc->sample_rate = OUT_RATE;
    O.aenc->sample_fmt = AV_SAMPLE_FMT_FLTP;
    av_channel_layout_default(&O.aenc->ch_layout, 2);
    O.aenc->bit_rate = 192000;
    O.aenc->time_base = (AVRational){ 1, OUT_RATE };
    if (avcodec_open2(O.aenc, ac, NULL) < 0) {
        logmsg("bdmenu: cannot open AAC encoder");
        return -1;
    }

    O.vst = avformat_new_stream(O.oc, NULL);
    avcodec_parameters_from_context(O.vst->codecpar, O.venc);
    O.vst->time_base = O.venc->time_base;
    O.ast = avformat_new_stream(O.oc, NULL);
    avcodec_parameters_from_context(O.ast->codecpar, O.aenc);
    O.ast->time_base = O.aenc->time_base;

    if (avio_open(&O.oc->pb, "pipe:1", AVIO_FLAG_WRITE) < 0) {
        return -1;
    }
    O.oc->flush_packets = 1;
    O.oc->max_delay = 100000;
    AVDictionary *mux = NULL;
    av_dict_set(&mux, "mpegts_flags", "resend_headers", 0);
    av_dict_set(&mux, "pat_period", "0.2", 0);
    if (avformat_write_header(O.oc, &mux) < 0) {
        av_dict_free(&mux);
        return -1;
    }
    av_dict_free(&mux);

    O.canvas_stride = OUT_W * 4;
    O.canvas = av_mallocz((size_t)O.canvas_stride * OUT_H);
    O.encframe = av_frame_alloc();
    O.encframe->format = O.venc->pix_fmt;
    O.encframe->width = OUT_W;
    O.encframe->height = OUT_H;
    av_frame_get_buffer(O.encframe, 0);
    O.to_enc = sws_getContext(OUT_W, OUT_H, AV_PIX_FMT_BGRA, OUT_W, OUT_H, O.venc->pix_fmt, SWS_BILINEAR, NULL, NULL, NULL);
    sws_setColorspaceDetails(O.to_enc, sws_getCoefficients(SWS_CS_ITU709), 1, sws_getCoefficients(SWS_CS_ITU709), 0, 0, 1 << 16, 1 << 16);

    O.aframe = av_frame_alloc();
    O.aout = av_audio_fifo_alloc(AV_SAMPLE_FMT_FLTP, 2, OUT_RATE);
    return 0;
}

static int write_packets(AVCodecContext *enc, AVStream *st)
{
    AVPacket *pkt = av_packet_alloc();
    int ret;
    while ((ret = avcodec_receive_packet(enc, pkt)) == 0) {
        av_packet_rescale_ts(pkt, enc->time_base, st->time_base);
        pkt->stream_index = st->index;
        if (av_interleaved_write_frame(O.oc, pkt) < 0) {
            av_packet_free(&pkt);
            return -1; /* reader went away */
        }
    }
    av_packet_free(&pkt);
    return 0;
}

/* ------------------------------------------------------------------ decoding */

static enum AVCodecID video_codec_id(int coding)
{
    switch (coding) {
    case 0x01: return AV_CODEC_ID_MPEG1VIDEO;
    case 0x02: return AV_CODEC_ID_MPEG2VIDEO;
    case 0x1b: return AV_CODEC_ID_H264;
    case 0x24: return AV_CODEC_ID_HEVC;
    case 0xea: return AV_CODEC_ID_VC1;
    default: return AV_CODEC_ID_NONE;
    }
}

static enum AVCodecID audio_codec_id(int coding)
{
    switch (coding) {
    case 0x80: return AV_CODEC_ID_PCM_BLURAY;
    case 0x81: return AV_CODEC_ID_AC3;
    case 0x83: return AV_CODEC_ID_AC3; /* TrueHD streams carry an AC-3 core */
    case 0x84: case 0xa1: return AV_CODEC_ID_EAC3;
    case 0x82: case 0x85: case 0x86: case 0xa2: return AV_CODEC_ID_DTS;
    case 0x03: case 0x04: return AV_CODEC_ID_MP2;
    default: return AV_CODEC_ID_NONE;
    }
}

static void close_decoder(AVCodecContext **ctx, AVCodecParserContext **parser)
{
    if (*parser) {
        av_parser_close(*parser);
        *parser = NULL;
    }
    avcodec_free_context(ctx);
}

static int open_decoder(AVCodecContext **ctx, AVCodecParserContext **parser, enum AVCodecID id)
{
    const AVCodec *c = avcodec_find_decoder(id);
    if (!c) {
        logmsg("bdmenu: no decoder for codec %d", (int)id);
        return -1;
    }
    *ctx = avcodec_alloc_context3(c);
    (*ctx)->pkt_timebase = (AVRational){ 1, 90000 };
    (*ctx)->thread_count = 0; /* auto */
    if (id == AV_CODEC_ID_PCM_BLURAY) {
        *parser = NULL; /* whole PES payloads, with their 4-byte header */
    } else {
        *parser = av_parser_init(id);
    }
    if (avcodec_open2(*ctx, c, NULL) < 0) {
        avcodec_free_context(ctx);
        return -1;
    }
    return 0;
}

static void clear_video_queue(void)
{
    for (int i = 0; i < P.vq_count; i++) {
        av_frame_free(&P.vq[i].frame);
    }
    P.vq_count = 0;
}

/* Drop everything buffered: after a seek, a new playlist or a discontinuity. */
static void flush_pipeline(int drop_queued)
{
    P.vpes.len = P.apes.len = 0;
    P.vpes.active = P.apes.active = 0;
    if (P.vdec) {
        avcodec_flush_buffers(P.vdec);
    }
    if (P.adec) {
        avcodec_flush_buffers(P.adec);
    }
    if (P.vparser) {
        av_parser_close(P.vparser);
        P.vparser = av_parser_init(video_codec_id(P.video_coding));
    }
    if (P.aparser) {
        av_parser_close(P.aparser);
        P.aparser = av_parser_init(audio_codec_id(P.audio_coding));
    }
    if (drop_queued) {
        clear_video_queue();
        if (P.afifo) {
            av_audio_fifo_reset(P.afifo);
        }
    }
    P.resync = 1;
}

/* Maps a stream timestamp to output-clock time. */
static double show_time(int64_t pts90)
{
    double now = now_s();
    if (pts90 == AV_NOPTS_VALUE) {
        return P.last_show > now ? P.last_show : now;
    }
    if (!P.resync) {
        double t = P.base_clock + (pts90 - P.base_pts) / 90000.0;
        if (t < now - 1.0 || t > now + 10.0) {
            P.resync = 1; /* timestamps jumped */
        } else {
            return t;
        }
    }
    P.resync = 0;
    P.base_pts = pts90;
    P.base_clock = now + 0.2;
    if (P.last_show > P.base_clock) {
        P.base_clock = P.last_show + 0.001;
    }
    return P.base_clock;
}

static void on_video_frame(AVFrame *f)
{
    int64_t pts = f->best_effort_timestamp;
    double t = show_time(pts);
    if (P.vq_count == VQ_MAX) {
        av_frame_free(&P.vq[0].frame);
        memmove(&P.vq[0], &P.vq[1], sizeof(P.vq[0]) * (VQ_MAX - 1));
        P.vq_count--;
    }
    P.vq[P.vq_count].frame = av_frame_clone(f);
    P.vq[P.vq_count].show = t;
    P.vq[P.vq_count].pts = pts;
    P.vq_count++;
    if (t > P.last_show) {
        P.last_show = t;
    }
}

static void on_audio_frame(AVFrame *f)
{
    if (!P.swr || P.swr_in_rate != f->sample_rate || P.swr_in_fmt != f->format || av_channel_layout_compare(&P.swr_in_layout, &f->ch_layout)) {
        swr_free(&P.swr);
        AVChannelLayout out = AV_CHANNEL_LAYOUT_STEREO;
        if (swr_alloc_set_opts2(&P.swr, &out, AV_SAMPLE_FMT_FLTP, OUT_RATE, &f->ch_layout, f->format, f->sample_rate, 0, NULL) < 0 || swr_init(P.swr) < 0) {
            swr_free(&P.swr);
            return;
        }
        av_channel_layout_uninit(&P.swr_in_layout);
        av_channel_layout_copy(&P.swr_in_layout, &f->ch_layout);
        P.swr_in_rate = f->sample_rate;
        P.swr_in_fmt = f->format;
    }

    int max_out = swr_get_out_samples(P.swr, f->nb_samples);
    uint8_t *out[2] = { NULL, NULL };
    if (av_samples_alloc(out, NULL, 2, max_out, AV_SAMPLE_FMT_FLTP, 0) < 0) {
        return;
    }
    int n = swr_convert(P.swr, out, max_out, (const uint8_t **)f->extended_data, f->nb_samples);
    if (n > 0) {
        double t = show_time(f->best_effort_timestamp);
        int queued = av_audio_fifo_size(P.afifo);
        if (queued == 0) {
            P.afifo_start = t;
            av_audio_fifo_write(P.afifo, (void **)out, n);
        } else {
            double expected = P.afifo_start + (double)queued / OUT_RATE;
            if (t - expected > 0.1) {
                /* gap in the stream: pad with silence so audio stays in step */
                int gap = (int)((t - expected) * OUT_RATE);
                if (gap > OUT_RATE * 2) {
                    gap = OUT_RATE * 2;
                }
                uint8_t *sil[2] = { NULL, NULL };
                if (av_samples_alloc(sil, NULL, 2, gap, AV_SAMPLE_FMT_FLTP, 0) >= 0) {
                    av_samples_set_silence(sil, 0, gap, 2, AV_SAMPLE_FMT_FLTP);
                    av_audio_fifo_write(P.afifo, (void **)sil, gap);
                    av_freep(&sil[0]);
                }
                av_audio_fifo_write(P.afifo, (void **)out, n);
            } else if (t - expected > -0.2) {
                av_audio_fifo_write(P.afifo, (void **)out, n);
            }
            /* else: overlaps what we already have, drop it */
        }
        /* never hold more than 3 s */
        int over = av_audio_fifo_size(P.afifo) - OUT_RATE * 3;
        if (over > 0) {
            av_audio_fifo_drain(P.afifo, over);
            P.afifo_start += (double)over / OUT_RATE;
        }
    }
    av_freep(&out[0]);
}

static void decode_packet(AVCodecContext *ctx, const uint8_t *data, int size, int64_t pts, int is_video)
{
    AVPacket *pkt = av_packet_alloc();
    pkt->data = (uint8_t *)data;
    pkt->size = size;
    pkt->pts = pts;
    pkt->dts = AV_NOPTS_VALUE;
    if (avcodec_send_packet(ctx, pkt) < 0) {
        av_packet_free(&pkt);
        return;
    }
    av_packet_free(&pkt);
    AVFrame *f = av_frame_alloc();
    while (avcodec_receive_frame(ctx, f) == 0) {
        if (is_video) {
            on_video_frame(f);
        } else {
            on_audio_frame(f);
        }
        av_frame_unref(f);
    }
    av_frame_free(&f);
}

static void feed_es(AVCodecContext *ctx, AVCodecParserContext *parser, const uint8_t *data, int size, int64_t pts, int is_video)
{
    if (!ctx) {
        return;
    }
    if (!parser) {
        decode_packet(ctx, data, size, pts, is_video);
        return;
    }
    while (size > 0) {
        uint8_t *out = NULL;
        int out_size = 0;
        int used = av_parser_parse2(parser, ctx, &out, &out_size, data, size, pts, AV_NOPTS_VALUE, 0);
        if (used < 0) {
            return;
        }
        data += used;
        size -= used;
        pts = AV_NOPTS_VALUE;
        if (out_size > 0) {
            decode_packet(ctx, out, out_size, parser->pts, is_video);
        }
    }
}

/* Parses a complete PES packet and hands its payload to the decoder. */
static void pes_flush(Pes *pes, int is_video)
{
    const uint8_t *b = pes->buf;
    int len = pes->len;
    pes->len = 0;
    if (len < 9 || b[0] != 0 || b[1] != 0 || b[2] != 1) {
        return;
    }
    int sid = b[3];
    int flags = b[7];
    int hdr_len = b[8];
    int payload = 9 + hdr_len;
    if (payload > len) {
        return;
    }
    int64_t pts = AV_NOPTS_VALUE;
    int p = 9;
    if (flags & 0x80) {
        pts = ((int64_t)(b[9] & 0x0e) << 29) | ((int64_t)b[10] << 22) | ((int64_t)(b[11] & 0xfe) << 14) | ((int64_t)b[12] << 7) | (b[13] >> 1);
        p += 5;
        if (flags & 0x40) {
            p += 5;
        }
    }
    int ext_id = -1;
    if (sid == 0xfd && (flags & 0x01)) {
        if (flags & 0x20) p += 6;
        if (flags & 0x10) p += 3;
        if (flags & 0x08) p += 1;
        if (flags & 0x04) p += 1;
        if (flags & 0x02) p += 2;
        if (p < payload) {
            int ef = b[p++];
            if (ef & 0x80) p += 16;
            if ((ef & 0x40) && p < payload) p += 1 + b[p];
            if (ef & 0x20) p += 2;
            if (ef & 0x10) p += 2;
            if ((ef & 0x01) && p + 1 < payload) ext_id = b[p + 1] & 0x7f;
        }
    }

    if (is_video) {
        feed_es(P.vdec, P.vparser, b + payload, len - payload, pts, 1);
    } else {
        if (P.audio_coding == 0x83 && ext_id != -1 && ext_id != 0x76) {
            return; /* TrueHD substream: keep only the AC-3 core */
        }
        feed_es(P.adec, P.aparser, b + payload, len - payload, pts, 0);
    }
}

static void pes_append(Pes *pes, const uint8_t *d, int n)
{
    if (pes->len + n > pes->cap) {
        int cap = (pes->len + n) * 2;
        if (cap > 8 * 1024 * 1024) {
            pes->len = 0; /* runaway: drop */
            return;
        }
        pes->buf = av_realloc(pes->buf, cap);
        pes->cap = cap;
    }
    memcpy(pes->buf + pes->len, d, n);
    pes->len += n;
}

static void ts_packet(const uint8_t *p)
{
    if (p[0] != 0x47) {
        return;
    }
    int pid = ((p[1] & 0x1f) << 8) | p[2];
    int pusi = p[1] & 0x40;
    int afc = (p[3] >> 4) & 3;
    int off = 4;
    if (afc & 2) {
        off += 1 + p[4];
    }
    if (!(afc & 1) || off >= 188) {
        return;
    }
    Pes *pes;
    int is_video;
    if (pid == P.video_pid && P.video_pid) {
        pes = &P.vpes;
        is_video = 1;
    } else if (pid == P.audio_pid && P.audio_pid) {
        pes = &P.apes;
        is_video = 0;
    } else {
        return;
    }
    if (pusi) {
        if (pes->active && pes->len) {
            pes_flush(pes, is_video);
        }
        pes->active = 1;
        pes->len = 0;
    }
    if (pes->active) {
        pes_append(pes, p + off, 188 - off);
    }
}

/* ------------------------------------------------------------------ disc state */

static const char *coding_name(int c)
{
    switch (c) {
    case 0x80: return "lpcm";
    case 0x81: return "ac3";
    case 0x82: return "dts";
    case 0x83: return "truehd";
    case 0x84: case 0xa1: return "eac3";
    case 0x85: return "dts-hd";
    case 0x86: return "dts-hd ma";
    case 0xa2: return "dts-hd";
    default: return "";
    }
}

static void emit_streams(void)
{
    if (!P.ti || P.playitem < 0 || (uint32_t)P.playitem >= P.ti->clip_count) {
        return;
    }
    const BLURAY_CLIP_INFO *c = &P.ti->clips[P.playitem];
    char buf[2048];
    int n = snprintf(buf, sizeof buf, "\"audio_streams\":[");
    for (int i = 0; i < c->audio_stream_count && n < (int)sizeof buf - 64; i++) {
        n += snprintf(buf + n, sizeof buf - n, "%s{\"lang\":\"%.3s\",\"codec\":\"%s\"}", i ? "," : "", (const char *)c->audio_streams[i].lang, coding_name(c->audio_streams[i].coding_type));
    }
    n += snprintf(buf + n, sizeof buf - n, "],\"pg_streams\":[");
    for (int i = 0; i < c->pg_stream_count && n < (int)sizeof buf - 32; i++) {
        n += snprintf(buf + n, sizeof buf - n, "%s{\"lang\":\"%.3s\"}", i ? "," : "", (const char *)c->pg_streams[i].lang);
    }
    snprintf(buf + n, sizeof buf - n, "]");
    emit("%s", buf);
}

/* Picks the video PID and the selected audio PID for the current play item and opens decoders. */
static void update_streams(void)
{
    int vpid = 0, vcoding = 0, apid = 0, acoding = 0;
    if (P.ti && P.playitem >= 0 && (uint32_t)P.playitem < P.ti->clip_count) {
        const BLURAY_CLIP_INFO *c = &P.ti->clips[P.playitem];
        if (c->video_stream_count > 0) {
            vpid = c->video_streams[0].pid;
            vcoding = c->video_streams[0].coding_type;
        }
        int a = P.audio_number - 1;
        if (a < 0 || a >= c->audio_stream_count) {
            a = 0;
        }
        if (c->audio_stream_count > 0) {
            apid = c->audio_streams[a].pid;
            acoding = c->audio_streams[a].coding_type;
        }
    }

    if (vcoding != P.video_coding) {
        close_decoder(&P.vdec, &P.vparser);
        if (vcoding && video_codec_id(vcoding) != AV_CODEC_ID_NONE) {
            open_decoder(&P.vdec, &P.vparser, video_codec_id(vcoding));
        }
        P.video_coding = vcoding;
    }
    if (acoding != P.audio_coding || apid != P.audio_pid) {
        close_decoder(&P.adec, &P.aparser);
        if (acoding && audio_codec_id(acoding) != AV_CODEC_ID_NONE) {
            open_decoder(&P.adec, &P.aparser, audio_codec_id(acoding));
        }
        P.audio_coding = acoding;
        P.apes.len = 0;
        P.apes.active = 0;
    }
    P.video_pid = vpid;
    P.audio_pid = apid;
}

static void handle_event(const BD_EVENT *ev)
{
    switch (ev->event) {
    case BD_EVENT_NONE:
        break;
    case BD_EVENT_ERROR:
    case BD_EVENT_READ_ERROR:
        emit("\"error\":\"disc read error (%u)\"", ev->param);
        break;
    case BD_EVENT_ENCRYPTED:
        emit("\"error\":\"encrypted content: this disc is not decrypted\"");
        break;
    case BD_EVENT_TITLE:
        P.title = (int)ev->param;
        emit("\"title\":%u", ev->param);
        break;
    case BD_EVENT_PLAYLIST:
        if (P.ti) {
            bd_free_title_info(P.ti);
        }
        P.ti = bd_get_playlist_info(P.bd, ev->param, 0);
        P.playitem = 0;
        update_streams();
        flush_pipeline(1);
        emit("\"playlist\":%u", ev->param);
        emit_streams();
        break;
    case BD_EVENT_PLAYITEM:
        P.playitem = (int)ev->param;
        update_streams();
        emit_streams();
        break;
    case BD_EVENT_CHAPTER:
        P.chapter = (int)ev->param;
        emit("\"chapter\":%u", ev->param);
        break;
    case BD_EVENT_SEEK:
    case BD_EVENT_DISCONTINUITY:
        flush_pipeline(ev->event == BD_EVENT_SEEK);
        break;
    case BD_EVENT_AUDIO_STREAM:
        if (ev->param != 0xff) {
            P.audio_number = (int)ev->param;
            update_streams();
            emit("\"audio\":%u", ev->param);
        }
        break;
    case BD_EVENT_PG_TEXTST:
        P.pg_enabled = (int)ev->param;
        emit("\"pg_enabled\":%s", ev->param ? "true" : "false");
        break;
    case BD_EVENT_PG_TEXTST_STREAM:
        P.pg_number = (int)ev->param;
        emit("\"pg\":%u", ev->param);
        break;
    case BD_EVENT_MENU:
        P.in_menu = (int)ev->param;
        emit("\"menu\":%s", ev->param ? "true" : "false");
        if (ev->param && opt.auto_enter > 0 && !P.user_input_seen && P.auto_enter_at == 0) {
            P.auto_enter_at = now_s() + opt.auto_enter;
        }
        break;
    case BD_EVENT_POPUP:
        P.popup = (int)ev->param;
        emit("\"popup\":%s", ev->param ? "true" : "false");
        break;
    case BD_EVENT_STILL:
        P.still = (int)ev->param;
        emit("\"still\":%s", ev->param ? "true" : "false");
        break;
    case BD_EVENT_STILL_TIME:
        if (ev->param) {
            P.still_until = now_s() + ev->param;
        } else {
            P.still_until = 0; /* infinite: wait for the user */
        }
        P.still = 1;
        break;
    case BD_EVENT_END_OF_TITLE:
        emit("\"end_of_title\":true");
        break;
    case BD_EVENT_IDLE:
        P.next_read = now_s() + 0.02;
        break;
    default:
        break;
    }
}

/* Reads one chunk from the disc. Returns bytes read (0 if nothing now, <0 at the end). */
static int read_disc(void)
{
    static uint8_t buf[READ_CHUNK];
    BD_EVENT ev;
    int n = bd_read_ext(P.bd, buf, sizeof buf, &ev);
    handle_event(&ev);
    while (bd_get_event(P.bd, &ev)) {
        handle_event(&ev);
    }
    if (n <= 0) {
        if (n < 0) {
            return -1;
        }
        P.next_read = now_s() + 0.01;
        return 0;
    }
    P.still = 0;
    for (int off = 0; off + 192 <= n; off += 192) {
        ts_packet(buf + off + 4);
    }
    return n;
}

/* ------------------------------------------------------------------ commands */

static int64_t cur_pts(void) { return P.shown_pts == AV_NOPTS_VALUE ? -1 : P.shown_pts; }

static void do_key(const char *k)
{
    static const struct { const char *name; uint32_t vk; } map[] = {
        { "up", BD_VK_UP }, { "down", BD_VK_DOWN }, { "left", BD_VK_LEFT }, { "right", BD_VK_RIGHT },
        { "enter", BD_VK_ENTER }, { "popup", BD_VK_POPUP },
        { "red", BD_VK_RED }, { "green", BD_VK_GREEN }, { "yellow", BD_VK_YELLOW }, { "blue", BD_VK_BLUE },
        { "0", BD_VK_0 }, { "1", BD_VK_1 }, { "2", BD_VK_2 }, { "3", BD_VK_3 }, { "4", BD_VK_4 },
        { "5", BD_VK_5 }, { "6", BD_VK_6 }, { "7", BD_VK_7 }, { "8", BD_VK_8 }, { "9", BD_VK_9 },
    };
    for (size_t i = 0; i < sizeof map / sizeof map[0]; i++) {
        if (!strcmp(k, map[i].name)) {
            bd_user_input(P.bd, cur_pts(), map[i].vk);
            return;
        }
    }
    if (!strcmp(k, "menu") || !strcmp(k, "stop")) {
        bd_menu_call(P.bd, cur_pts());
    } else if (!strcmp(k, "back")) {
        if (P.popup) {
            bd_user_input(P.bd, cur_pts(), BD_VK_POPUP);
        }
    } else if (!strcmp(k, "pause") || !strcmp(k, "play")) {
        P.paused = !strcmp(k, "pause") ? !P.paused : 0;
        P.resync = 1;
        emit("\"paused\":%s", P.paused ? "true" : "false");
    } else if (!strcmp(k, "next")) {
        unsigned c = bd_get_current_chapter(P.bd); /* 1-based */
        bd_seek_chapter(P.bd, c);
    } else if (!strcmp(k, "prev")) {
        unsigned c = bd_get_current_chapter(P.bd);
        bd_seek_chapter(P.bd, c >= 2 ? c - 2 : 0);
    }
}

static void handle_command(const char *line)
{
    char a[32] = { 0 }, b[32] = { 0 }, c[32] = { 0 };
    if (sscanf(line, "%31s %31s %31s", a, b, c) < 1) {
        return;
    }
    if (strcmp(a, "quit")) {
        P.user_input_seen = 1;
        P.auto_enter_at = 0;
    }
    if (!strcmp(a, "quit")) {
        quit_flag = 1;
    } else if (!strcmp(a, "key")) {
        do_key(b);
    } else if (!strcmp(a, "click") || !strcmp(a, "move")) {
        double x = atof(b), y = atof(c);
        int w = planes[1].w ? planes[1].w : OUT_W, h = planes[1].h ? planes[1].h : OUT_H;
        uint16_t px = (uint16_t)(x * (w - 1)), py = (uint16_t)(y * (h - 1));
        bd_mouse_select(P.bd, cur_pts(), px, py);
        if (!strcmp(a, "click")) {
            bd_user_input(P.bd, cur_pts(), BD_VK_MOUSE_ACTIVATE);
        }
    } else if (!strcmp(a, "title")) {
        bd_play_title(P.bd, (unsigned)atoi(b));
    } else if (!strcmp(a, "audio")) {
        bd_select_stream(P.bd, BLURAY_AUDIO_STREAM, (uint32_t)atoi(b), 1);
    } else if (!strcmp(a, "pg")) {
        int n = atoi(b);
        if (n <= 0) {
            bd_select_stream(P.bd, BLURAY_PG_TEXTST_STREAM, P.pg_number > 0 ? (uint32_t)P.pg_number : 1, 0);
        } else {
            bd_select_stream(P.bd, BLURAY_PG_TEXTST_STREAM, (uint32_t)n, 1);
        }
    } else if (!strcmp(a, "chapter")) {
        do_key(b[0] == '-' ? "prev" : "next");
    }
}

/* ------------------------------------------------------------------ output tick */

static int output_tick(double tick_time)
{
    /* pick the newest frame that is due */
    while (P.vq_count > 0 && P.vq[0].show <= tick_time) {
        av_frame_free(&P.shown);
        P.shown = P.vq[0].frame;
        P.shown_pts = P.vq[0].pts;
        memmove(&P.vq[0], &P.vq[1], sizeof(P.vq[0]) * (P.vq_count - 1));
        P.vq_count--;
    }
    if (P.shown_pts != AV_NOPTS_VALUE) {
        bd_set_scr(P.bd, P.shown_pts);
    }

    /* video: frame (or black) -> canvas -> overlays -> encoder */
    if (P.shown) {
        AVFrame *f = P.shown;
        if (!O.to_canvas || O.to_canvas_w != f->width || O.to_canvas_h != f->height || O.to_canvas_fmt != f->format) {
            sws_freeContext(O.to_canvas);
            O.to_canvas = sws_getContext(f->width, f->height, f->format, OUT_W, OUT_H, AV_PIX_FMT_BGRA, SWS_BILINEAR, NULL, NULL, NULL);
            int src_cs = f->height > 576 ? SWS_CS_ITU709 : SWS_CS_ITU601;
            sws_setColorspaceDetails(O.to_canvas, sws_getCoefficients(src_cs), 0, sws_getCoefficients(SWS_CS_ITU709), 1, 0, 1 << 16, 1 << 16);
            O.to_canvas_w = f->width;
            O.to_canvas_h = f->height;
            O.to_canvas_fmt = f->format;
        }
        uint8_t *dst[1] = { O.canvas };
        int dst_stride[1] = { O.canvas_stride };
        sws_scale(O.to_canvas, (const uint8_t *const *)f->data, f->linesize, 0, f->height, dst, dst_stride);
    } else {
        for (int i = 0; i < OUT_W * OUT_H; i++) {
            ((uint32_t *)O.canvas)[i] = 0xff000000u;
        }
    }

    pthread_mutex_lock(&plane_lock);
    blend_plane(O.canvas, O.canvas_stride, &planes[0]);
    blend_plane(O.canvas, O.canvas_stride, &planes[1]);
    pthread_mutex_unlock(&plane_lock);

    av_frame_make_writable(O.encframe);
    const uint8_t *src[1] = { O.canvas };
    int src_stride[1] = { O.canvas_stride };
    sws_scale(O.to_enc, src, src_stride, 0, OUT_H, O.encframe->data, O.encframe->linesize);
    O.encframe->pts = O.vframes++;
    if (avcodec_send_frame(O.venc, O.encframe) < 0 || write_packets(O.venc, O.vst) < 0) {
        return -1;
    }

    /* audio: exactly one frame's worth of samples per tick, silence where there is none */
    int64_t want_total = av_rescale_q(O.vframes, av_inv_q(opt.fps), (AVRational){ 1, OUT_RATE });
    int need = (int)(want_total - O.asamples - av_audio_fifo_size(O.aout));
    if (need > 0) {
        uint8_t *tmp[2] = { NULL, NULL };
        av_samples_alloc(tmp, NULL, 2, need, AV_SAMPLE_FMT_FLTP, 0);
        av_samples_set_silence(tmp, 0, need, 2, AV_SAMPLE_FMT_FLTP);
        int have = av_audio_fifo_size(P.afifo);
        if (!P.paused && have > 0) {
            /* drop audio that is already late */
            double late = tick_time - P.afifo_start;
            if (late > 0.1) {
                int drop = (int)(late * OUT_RATE);
                if (drop > have) {
                    drop = have;
                }
                av_audio_fifo_drain(P.afifo, drop);
                P.afifo_start += (double)drop / OUT_RATE;
                have -= drop;
            }
            int lead = 0;
            if (P.afifo_start > tick_time) {
                lead = (int)((P.afifo_start - tick_time) * OUT_RATE); /* silence before the audio starts */
            }
            if (lead < need && have > 0) {
                int take = need - lead;
                if (take > have) {
                    take = have;
                }
                uint8_t *at[2] = { tmp[0] + (size_t)lead * sizeof(float), tmp[1] + (size_t)lead * sizeof(float) };
                av_audio_fifo_read(P.afifo, (void **)at, take);
                P.afifo_start += (double)take / OUT_RATE;
            }
        }
        av_audio_fifo_write(O.aout, (void **)tmp, need);
        av_freep(&tmp[0]);
    }
    while (av_audio_fifo_size(O.aout) >= O.aenc->frame_size) {
        AVFrame *af = O.aframe;
        av_frame_unref(af);
        af->nb_samples = O.aenc->frame_size;
        af->format = AV_SAMPLE_FMT_FLTP;
        af->sample_rate = OUT_RATE;
        av_channel_layout_copy(&af->ch_layout, &O.aenc->ch_layout);
        av_frame_get_buffer(af, 0);
        av_audio_fifo_read(O.aout, (void **)af->data, af->nb_samples);
        af->pts = O.asamples;
        O.asamples += af->nb_samples;
        if (avcodec_send_frame(O.aenc, af) < 0 || write_packets(O.aenc, O.ast) < 0) {
            return -1;
        }
    }
    return 0;
}

/* ------------------------------------------------------------------ setup */

static int open_disc(void)
{
    P.bd = bd_open(opt.disc, NULL);
    if (!P.bd) {
        emit("\"error\":\"cannot open disc\"");
        return -1;
    }
    const BLURAY_DISC_INFO *di = bd_get_disc_info(P.bd);
    if (!di || !di->bluray_detected) {
        emit("\"error\":\"not a Blu-ray disc\"");
        return -1;
    }
    if (di->aacs_detected && !di->aacs_handled) {
        emit("\"error\":\"this disc is AACS encrypted; only decrypted copies are supported\"");
        return -3;
    }
    if (di->bdplus_detected && !di->bdplus_handled) {
        emit("\"error\":\"this disc uses BD+; only decrypted copies are supported\"");
        return -3;
    }
    emit("\"bdj\":%s,\"bdj_handled\":%s,\"jvm\":%s,\"first_play\":%s,\"top_menu\":%s",
         di->bdj_detected ? "true" : "false", di->bdj_handled ? "true" : "false",
         di->libjvm_detected ? "true" : "false",
         di->first_play_supported ? "true" : "false", di->top_menu_supported ? "true" : "false");
    if (di->bdj_detected && !di->bdj_handled) {
        emit("\"warning\":\"BD-J menus need Java: %s\"", di->libjvm_detected ? "the BD-J jar was not found" : "no Java runtime found");
    }
    return 0;
}

static void apply_settings(void)
{
    bd_set_player_setting(P.bd, BLURAY_PLAYER_SETTING_REGION_CODE, (uint32_t)opt.region);
    bd_set_player_setting(P.bd, BLURAY_PLAYER_SETTING_PARENTAL, 99);
    bd_set_player_setting(P.bd, BLURAY_PLAYER_SETTING_DECODE_PG, 1);
    bd_set_player_setting(P.bd, BLURAY_PLAYER_SETTING_PERSISTENT_STORAGE, 1);
    bd_set_player_setting_str(P.bd, BLURAY_PLAYER_SETTING_AUDIO_LANG, opt.lang);
    bd_set_player_setting_str(P.bd, BLURAY_PLAYER_SETTING_PG_LANG, opt.lang);
    bd_set_player_setting_str(P.bd, BLURAY_PLAYER_SETTING_MENU_LANG, opt.lang);
    bd_set_player_setting_str(P.bd, BLURAY_PLAYER_SETTING_COUNTRY_CODE, "us");
    if (opt.persist) {
        bd_set_player_setting_str(P.bd, BLURAY_PLAYER_PERSISTENT_ROOT, opt.persist);
    }
    if (opt.cache) {
        bd_set_player_setting_str(P.bd, BLURAY_PLAYER_CACHE_ROOT, opt.cache);
    }
#ifdef BLURAY_PLAYER_JAVA_HOME
    if (getenv("JAVA_HOME")) {
        bd_set_player_setting_str(P.bd, BLURAY_PLAYER_JAVA_HOME, getenv("JAVA_HOME"));
    }
#endif
}

static int run_probe(void)
{
    int r = open_disc();
    if (r < 0) {
        return -r;
    }
    uint32_t n = bd_get_titles(P.bd, TITLES_RELEVANT, 0);
    const BLURAY_DISC_INFO *di = bd_get_disc_info(P.bd);
    printf("{\"bdj\":%s,\"titles\":[", di->bdj_detected ? "true" : "false");
    for (uint32_t i = 0; i < n; i++) {
        BLURAY_TITLE_INFO *ti = bd_get_title_info(P.bd, i, 0);
        if (!ti) {
            continue;
        }
        printf("%s{\"index\":%u,\"playlist\":%u,\"duration\":%.3f,\"chapters\":%u}", i ? "," : "", i, ti->playlist, ti->duration / 90000.0, ti->chapter_count);
        bd_free_title_info(ti);
    }
    printf("]}\n");
    bd_close(P.bd);
    return 0;
}

static void usage(void)
{
    fprintf(stderr, "usage: bdmenu --probe|--menu --disc PATH [--vbitrate KBPS] [--preset P] [--encoder E] [--region A|B|C]\n"
                    "              [--lang eng] [--persist DIR] [--cache DIR] [--auto-enter S] [--no-bdj] [--fps N/D]\n");
}

static int parse_args(int argc, char **argv)
{
    int mode = 0;
    for (int i = 1; i < argc; i++) {
        const char *a = argv[i];
        const char *v = i + 1 < argc ? argv[i + 1] : NULL;
        if (!strcmp(a, "--probe")) { opt.probe = 1; mode = 1; }
        else if (!strcmp(a, "--menu")) { mode = 1; }
        else if (!strcmp(a, "--no-bdj")) { opt.no_bdj = 1; }
        else if (!v) { return -1; }
        else if (!strcmp(a, "--disc")) { opt.disc = v; i++; }
        else if (!strcmp(a, "--vbitrate")) { opt.vbitrate_kbps = atoi(v); i++; }
        else if (!strcmp(a, "--preset")) { opt.preset = v; i++; }
        else if (!strcmp(a, "--encoder")) { opt.encoder = v; i++; }
        else if (!strcmp(a, "--region")) { opt.region = v[0] == 'B' ? 2 : v[0] == 'C' ? 4 : 1; i++; }
        else if (!strcmp(a, "--lang")) { opt.lang = v; i++; }
        else if (!strcmp(a, "--persist")) { opt.persist = v; i++; }
        else if (!strcmp(a, "--cache")) { opt.cache = v; i++; }
        else if (!strcmp(a, "--auto-enter")) { opt.auto_enter = atoi(v); i++; }
        else if (!strcmp(a, "--fps")) {
            int n = 0, d = 1;
            if (sscanf(v, "%d/%d", &n, &d) >= 1 && n > 0 && d > 0) { opt.fps = (AVRational){ n, d }; }
            i++;
        }
        else { return -1; }
    }
    if (!mode || !opt.disc) {
        return -1;
    }
    if (opt.vbitrate_kbps < 500) {
        opt.vbitrate_kbps = 500;
    }
    return 0;
}

int main(int argc, char **argv)
{
#ifdef _WIN32
    _setmode(_fileno(stdout), _O_BINARY);
#else
    signal(SIGPIPE, SIG_IGN);
#endif
    if (parse_args(argc, argv) < 0) {
        usage();
        return 2;
    }
    av_log_set_level(AV_LOG_WARNING);

    if (opt.probe) {
        return run_probe();
    }

    memset(&P, 0, sizeof P);
    P.shown_pts = AV_NOPTS_VALUE;
    P.audio_number = 1;
    P.playitem = -1;
    P.resync = 1;
    P.afifo = av_audio_fifo_alloc(AV_SAMPLE_FMT_FLTP, 2, OUT_RATE);

    int r = open_disc();
    if (r < 0) {
        return -r;
    }
    apply_settings();
    bd_register_overlay_proc(P.bd, NULL, overlay_cb);
    bd_register_argb_overlay_proc(P.bd, NULL, argb_overlay_cb, NULL);

    if (open_output() < 0) {
        emit("\"error\":\"cannot start the encoder\"");
        return 4;
    }

    pthread_t th;
    pthread_create(&th, NULL, stdin_thread, NULL);

    if (!bd_play(P.bd)) {
        emit("\"error\":\"the disc could not start (first play failed)\"");
        return 5;
    }
    emit("\"started\":true");

    const double tick = 1.0 / av_q2d(opt.fps);
    const double t0 = now_s();
    int64_t n = 0;
    char line[160];

    while (!quit_flag) {
        while (pop_command(line, sizeof line)) {
            handle_command(line);
        }

        double now = now_s();
        if (P.auto_enter_at > 0 && now >= P.auto_enter_at) {
            P.auto_enter_at = 0;
            P.user_input_seen = 1;
            bd_user_input(P.bd, cur_pts(), BD_VK_ENTER);
        }
        if (P.still && P.still_until > 0 && now >= P.still_until) {
            P.still_until = 0;
            bd_read_skip_still(P.bd);
        }

        /* read and decode ahead, up to about one second */
        int reads = 0;
        while (!P.paused && now >= P.next_read && reads < 16 &&
               P.vq_count < VQ_MAX - 2 && (P.vq_count == 0 || P.vq[P.vq_count - 1].show < now + 1.0) &&
               av_audio_fifo_size(P.afifo) < OUT_RATE * 2) {
            int got = read_disc();
            reads++;
            if (got < 0) {
                emit("\"ended\":true");
                quit_flag = 1;
                break;
            }
            if (got == 0) {
                break;
            }
        }

        double due = t0 + n * tick;
        if (now >= due) {
            if (now - due > 1.0) {
                n = (int64_t)((now - t0) / tick); /* fell behind: skip rather than burst */
                due = t0 + n * tick;
            }
            if (P.paused) {
                /* hold the picture: keep shifting queued frames forward */
                for (int i = 0; i < P.vq_count; i++) {
                    P.vq[i].show += tick;
                }
                P.base_clock += tick;
                P.afifo_start += tick;
            }
            if (output_tick(due) < 0) {
                break; /* the reader closed the pipe */
            }
            n++;
        } else {
            double wait = due - now;
            av_usleep((unsigned)(wait > 0.004 ? 4000 : wait * 1e6));
        }
    }

    av_write_trailer(O.oc);
    avio_closep(&O.oc->pb);
    bd_close(P.bd);
    return 0;
}

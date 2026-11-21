cmake_minimum_required(VERSION 3.16)
project(bdmenu C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
endif()

find_package(PkgConfig REQUIRED)
find_package(Threads REQUIRED)
pkg_check_modules(DEPS REQUIRED IMPORTED_TARGET
  libbluray>=1.3.0
  libavcodec
  libavformat
  libavutil
  libswscale
  libswresample)

add_executable(bdmenu bdmenu.c)
target_link_libraries(bdmenu PRIVATE PkgConfig::DEPS Threads::Threads)
if(NOT WIN32)
  target_link_libraries(bdmenu PRIVATE m)
endif()
if(MSVC)
  target_compile_options(bdmenu PRIVATE /W3)
else()
  target_compile_options(bdmenu PRIVATE -Wall -Wextra -Wno-unused-parameter)
endif()

# The bundle keeps shared libraries in ../lib next to bin/.
if(APPLE)
  set_target_properties(bdmenu PROPERTIES INSTALL_RPATH "@executable_path/../lib" BUILD_WITH_INSTALL_RPATH ON)
elseif(UNIX)
  set_target_properties(bdmenu PROPERTIES INSTALL_RPATH "\$ORIGIN/../lib" BUILD_WITH_INSTALL_RPATH ON)
endif()

install(TARGETS bdmenu RUNTIME DESTINATION bin)

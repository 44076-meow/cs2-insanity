## ReplicaHider — Metamod:Source 2.0 plugin for CS2 (Linux only).
##
## Builds a single shared object: build/replicahider.so
## Deploy: copy to game/csgo/addons/metamod/bin/linuxsteamrt64/
##         and the .vdf into game/csgo/addons/metamod/

NAME       := replicahider
BUILD      := build
OBJDIR     := $(BUILD)/obj
TARGET     := $(BUILD)/$(NAME).so

HL2SDK     := hl2sdk
MMSOURCE   := mmsource

CXX        ?= g++
CXXSTD     := -std=c++17

# Standard Source 2 / HL2SDK defines for Linux.
CXXDEFINES := \
	-DLINUX -D_LINUX -DPOSIX -DGNUC \
	-D_GLIBCXX_USE_CXX11_ABI=0 \
	-DCOMPILER_GCC=1 \
	-DSOURCE2 \
	-DPLATFORM_64BITS \
	-DSOURCEHOOK_BEING_COMPILED \
	-DMETA_IS_SOURCE2 \
	-Dstricmp=strcasecmp \
	-Dstrnicmp=strncasecmp \
	-D_stricmp=strcasecmp \
	-D_strnicmp=strncasecmp

CXXINCLUDES := \
	-Isrc \
	-I$(BUILD)/proto \
	-I$(MMSOURCE)/core \
	-I$(MMSOURCE)/core/sourcehook \
	-I$(MMSOURCE)/public \
	-I$(HL2SDK)/public \
	-I$(HL2SDK)/public/engine \
	-I$(HL2SDK)/public/mathlib \
	-I$(HL2SDK)/public/tier0 \
	-I$(HL2SDK)/public/tier1 \
	-I$(HL2SDK)/public/entity2 \
	-I$(HL2SDK)/public/game/server \
	-I$(HL2SDK)/public/game/shared \
	-I$(HL2SDK)/common \
	-I$(HL2SDK)/thirdparty/protobuf-3.21.8/src \
	-I$(HL2SDK)/game/server

CXXFLAGS := \
	$(CXXSTD) -fPIC -fvisibility=hidden -fvisibility-inlines-hidden \
	-fno-strict-aliasing -fno-exceptions -fno-rtti -fno-threadsafe-statics \
	-Wall -Wno-unknown-pragmas -Wno-attributes -Wno-non-virtual-dtor \
	-Wno-overloaded-virtual -Wno-invalid-offsetof -Wno-uninitialized \
	-Wno-unused -O2 -g \
	$(CXXDEFINES) $(CXXINCLUDES)

# --unresolved-symbols=ignore-in-object-files — engine binary symbols
# (ConVar_Register, ConCommand::Create, Plat_ExitProcess, …) are
# resolved at runtime from the already-loaded server process, not at
# our .so's load time. Without this the dynamic linker rejects load.
LDFLAGS := -shared -fPIC -static-libgcc -static-libstdc++ -ldl -lpthread \
	-Wl,--unresolved-symbols=ignore-in-object-files

# Source 2 SDK static libs. libprotobuf.a needed because eiface.h
# transitively includes generated network_connection.pb.h which links
# back to protobuf::Message etc.
LIBS := \
	-L$(HL2SDK)/lib/linux64 \
	-l:tier1.a -l:mathlib.a -l:interfaces.a \
	$(HL2SDK)/lib/linux64/release/libprotobuf.a \
	$(HL2SDK)/lib/linux64/libtier0.so

# Plugin sources + sourcehook compilation.
PLUGIN_SRCS := src/plugin.cpp src/pool.cpp src/aim_hook.cpp
PLUGIN_ASM_SRCS := src/aim_hook_asm.S
# sourcehook_hookmangen_x86_64.cpp is INCOMPLETE for Linux x64 in
# upstream metamod-source — static_assert(false, "Missing registers
# saving for linux") fires at compile time. We exclude it for now;
# this means *runtime* hook installation (SH_ADD_*) will fail, but
# the skeleton compiles and the .so loads. Wiring the actual hook
# requires either patching hookmangen's Linux x64 ABI path or using
# raw vtable-swap (next iteration).
SH_SRCS     := \
	$(MMSOURCE)/core/sourcehook/sourcehook.cpp \
	$(MMSOURCE)/core/sourcehook/sourcehook_impl_chookidman.cpp \
	$(MMSOURCE)/core/sourcehook/sourcehook_impl_chookmaninfo.cpp \
	$(MMSOURCE)/core/sourcehook/sourcehook_impl_cproto.cpp \
	$(MMSOURCE)/core/sourcehook/sourcehook_impl_cvfnptr.cpp

ALL_SRCS := $(PLUGIN_SRCS) $(SH_SRCS)
OBJS     := $(patsubst %.cpp,$(OBJDIR)/%.o,$(ALL_SRCS)) \
            $(patsubst %.S,$(OBJDIR)/%.o,$(PLUGIN_ASM_SRCS))

all: $(TARGET)

PROTOC := $(HL2SDK)/devtools/bin/linux/protoc
PROTO_OUT := $(BUILD)/proto/network_connection.pb.h
$(PROTO_OUT): $(HL2SDK)/common/network_connection.proto
	@mkdir -p $(BUILD)/proto
	$(PROTOC) -I$(HL2SDK)/common -I$(HL2SDK)/thirdparty/protobuf-3.21.8/src --cpp_out=$(BUILD)/proto $<

$(TARGET): $(PROTO_OUT) $(OBJS) | $(BUILD)
	$(CXX) $(LDFLAGS) -o $@ $(OBJS) $(LIBS)
	@echo "==> built $@"

$(OBJDIR)/%.o: %.cpp
	@mkdir -p $(dir $@)
	$(CXX) $(CXXFLAGS) -c $< -o $@

$(OBJDIR)/%.o: %.S
	@mkdir -p $(dir $@)
	$(CXX) -c $< -o $@

$(BUILD):
	@mkdir -p $@

clean:
	rm -rf $(BUILD)

.PHONY: all clean

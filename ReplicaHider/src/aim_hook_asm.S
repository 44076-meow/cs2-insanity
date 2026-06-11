// aim_hook_asm.S — naked entry stub for libserver.so:CCSBot::UpdateLookAngles.
//
// Entered via JMP from the patched function prologue. State on entry:
//   rdi = CCSBot* (this pointer; first arg in System V x86_64 ABI)
//   rsp = caller's frame (CCSBot::Upkeep's stack)
//   All caller-saved registers may already be live with arg/return values.
//
// We must:
//   1. Save the caller-saved register state (rax, rcx, rdx, rsi, rdi, r8-r11,
//      xmm0-xmm7) so the C handler can clobber them freely.
//   2. Reload rdi from the saved slot — the C ABI guarantees rdi is preserved
//      ACROSS the call but we save it on stack before the call so we can
//      restore it for the trampoline jump (UpdateLookAngles' real body
//      expects rdi=CCSBot* in its prologue).
//   3. Call aim_hook_handler(rdi).
//   4. Restore everything.
//   5. JMP to g_aim_trampoline_ptr — the trampoline runs the original 8-byte
//      prologue with the LEA's RIP-displacement adjusted, then JMPs to
//      target+8, where UpdateLookAngles continues normally with our
//      m_lookPitch/m_lookYaw overrides already in place.
//
// Stack layout after all saves (rsp grows DOWN):
//   [rsp + 192]   <- saved rax  (push order: rax first)
//   [rsp + 184]   <- saved rcx
//   [rsp + 176]   <- saved rdx
//   [rsp + 168]   <- saved rsi
//   [rsp + 160]   <- saved rdi    *** reload point for ABI arg
//   [rsp + 152]   <- saved r8
//   [rsp + 144]   <- saved r9
//   [rsp + 136]   <- saved r10
//   [rsp + 128]   <- saved r11
//   [rsp + 112]   <- xmm7
//   [rsp +  96]   <- xmm6
//   [rsp +  80]   <- xmm5
//   [rsp +  64]   <- xmm4
//   [rsp +  48]   <- xmm3
//   [rsp +  32]   <- xmm2
//   [rsp +  16]   <- xmm1
//   [rsp +   0]   <- xmm0
// Total saved: 9*8 + 8*16 = 72 + 128 = 200 bytes.
//
// Note on stack alignment: System V requires 16-byte alignment AT the call
// instruction. JMP-entry from the patched function leaves rsp at <something
// 16n + 8> (because the original CCSBot::Upkeep `call b41c40` pushed an 8-byte
// return address). Our 9 GP pushes leave rsp at <16n + 8 - 72> = <16n - 64> =
// 16-aligned. Then sub rsp,128 keeps it 16-aligned. So the call is aligned. ✓

    .text
    .intel_syntax noprefix

    .globl  aim_hook_naked_entry
    .type   aim_hook_naked_entry, @function
    .hidden aim_hook_naked_entry

aim_hook_naked_entry:
    push    rax
    push    rcx
    push    rdx
    push    rsi
    push    rdi             // saved at [rsp+160] after xmm sub
    push    r8
    push    r9
    push    r10
    push    r11
    sub     rsp, 128
    movdqu  XMMWORD PTR [rsp +   0], xmm0
    movdqu  XMMWORD PTR [rsp +  16], xmm1
    movdqu  XMMWORD PTR [rsp +  32], xmm2
    movdqu  XMMWORD PTR [rsp +  48], xmm3
    movdqu  XMMWORD PTR [rsp +  64], xmm4
    movdqu  XMMWORD PTR [rsp +  80], xmm5
    movdqu  XMMWORD PTR [rsp +  96], xmm6
    movdqu  XMMWORD PTR [rsp + 112], xmm7

    // First arg (rdi) — load CCSBot* back from saved slot. (rdi was clobberable
    // by anything the assembler/loader emitted between push rdi and now,
    // though in practice nothing did — but reloading is cheap and explicit.)
    mov     rdi, QWORD PTR [rsp + 160]

    call    aim_hook_handler@PLT

    movdqu  xmm0, XMMWORD PTR [rsp +   0]
    movdqu  xmm1, XMMWORD PTR [rsp +  16]
    movdqu  xmm2, XMMWORD PTR [rsp +  32]
    movdqu  xmm3, XMMWORD PTR [rsp +  48]
    movdqu  xmm4, XMMWORD PTR [rsp +  64]
    movdqu  xmm5, XMMWORD PTR [rsp +  80]
    movdqu  xmm6, XMMWORD PTR [rsp +  96]
    movdqu  xmm7, XMMWORD PTR [rsp + 112]
    add     rsp, 128
    pop     r11
    pop     r10
    pop     r9
    pop     r8
    pop     rdi             // restore CCSBot* — body of UpdateLookAngles needs it
    pop     rsi
    pop     rdx
    pop     rcx
    pop     rax

    // Tail-jump to trampoline. RIP-relative load of the global pointer
    // emitted by aim_hook.cpp. The trampoline contains the original first
    // 8 bytes (with adjusted LEA disp) followed by JMP back to target+8.
    jmp     QWORD PTR [rip + g_aim_trampoline_ptr]

    .size   aim_hook_naked_entry, .-aim_hook_naked_entry

    // Mark stack as non-executable for GNU stack policy compliance.
    .section .note.GNU-stack, "", @progbits

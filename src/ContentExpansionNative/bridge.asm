option casemap:none
EXTERN NativeBridgeContexts:BYTE
EXTERN __chkstk:PROC
EXTERN ObserveBridge:PROC
FRAME_SIZE EQU 36880
SAVED EQU FRAME_SIZE
UNWIND_XMM EQU 33280

SAVE_UNWIND_XMM MACRO
    movdqu [rsp+UNWIND_XMM+0],xmm6
    movdqu [rsp+UNWIND_XMM+16],xmm7
    movdqu [rsp+UNWIND_XMM+32],xmm8
    movdqu [rsp+UNWIND_XMM+48],xmm9
    movdqu [rsp+UNWIND_XMM+64],xmm10
    movdqu [rsp+UNWIND_XMM+80],xmm11
    movdqu [rsp+UNWIND_XMM+96],xmm12
    movdqu [rsp+UNWIND_XMM+112],xmm13
    movdqu [rsp+UNWIND_XMM+128],xmm14
    movdqu [rsp+UNWIND_XMM+144],xmm15
ENDM

SAVE_GP MACRO
    mov [rsp+SAVED+0],r15
    mov [rsp+SAVED+8],r14
    mov [rsp+SAVED+16],r13
    mov [rsp+SAVED+24],r12
    mov [rsp+SAVED+32],r11
    mov [rsp+SAVED+40],r10
    mov [rsp+SAVED+48],r9
    mov [rsp+SAVED+56],r8
    mov [rsp+SAVED+64],rdi
    mov [rsp+SAVED+72],rsi
    mov [rsp+SAVED+80],rbp
    mov [rsp+SAVED+88],rbx
    mov [rsp+SAVED+96],rdx
    mov [rsp+SAVED+104],rcx
    mov [rsp+SAVED+112],rax
ENDM
RESTORE_GP MACRO
    mov r15,[rsp+SAVED+0]
    mov r14,[rsp+SAVED+8]
    mov r13,[rsp+SAVED+16]
    mov r12,[rsp+SAVED+24]
    mov r11,[rsp+SAVED+32]
    mov r10,[rsp+SAVED+40]
    mov r9,[rsp+SAVED+48]
    mov r8,[rsp+SAVED+56]
    mov rdi,[rsp+SAVED+64]
    mov rsi,[rsp+SAVED+72]
    mov rbp,[rsp+SAVED+80]
    mov rbx,[rsp+SAVED+88]
    mov rdx,[rsp+SAVED+96]
    mov rcx,[rsp+SAVED+104]
    mov rax,[rsp+SAVED+112]
ENDM
SAVE_XSTATE MACRO
    mov r10,[rsp+168]
    mov r11,[rsp+160]
    mov eax,[r10+24]
    mov edx,[r10+28]
    xsave64 [r11]
ENDM
RESTORE_XSTATE MACRO
    mov r10,[rsp+168]
    mov r11,[rsp+160]
    mov eax,[r10+24]
    mov edx,[r10+28]
    xrstor64 [r11]
ENDM

.code
MAKE_BRIDGE MACRO n
LOCAL finish, call_native, no_override, copy_stack, stack_done
PUBLIC Bridge&n
Bridge&n PROC FRAME
    push rax
    .allocstack 8
    push rcx
    .allocstack 8
    push rdx
    .allocstack 8
    push rbx
    .pushreg rbx
    push rbp
    .pushreg rbp
    push rsi
    .pushreg rsi
    push rdi
    .pushreg rdi
    push r8
    .allocstack 8
    push r9
    .allocstack 8
    push r10
    .allocstack 8
    push r11
    .allocstack 8
    push r12
    .pushreg r12
    push r13
    .pushreg r13
    push r14
    .pushreg r14
    push r15
    .pushreg r15
    mov eax,FRAME_SIZE
    call __chkstk
    sub rsp,rax
    .allocstack FRAME_SIZE
    movdqu [rsp+UNWIND_XMM+0],xmm6
    .savexmm128 xmm6,UNWIND_XMM+0
    movdqu [rsp+UNWIND_XMM+16],xmm7
    .savexmm128 xmm7,UNWIND_XMM+16
    movdqu [rsp+UNWIND_XMM+32],xmm8
    .savexmm128 xmm8,UNWIND_XMM+32
    movdqu [rsp+UNWIND_XMM+48],xmm9
    .savexmm128 xmm9,UNWIND_XMM+48
    movdqu [rsp+UNWIND_XMM+64],xmm10
    .savexmm128 xmm10,UNWIND_XMM+64
    movdqu [rsp+UNWIND_XMM+80],xmm11
    .savexmm128 xmm11,UNWIND_XMM+80
    movdqu [rsp+UNWIND_XMM+96],xmm12
    .savexmm128 xmm12,UNWIND_XMM+96
    movdqu [rsp+UNWIND_XMM+112],xmm13
    .savexmm128 xmm13,UNWIND_XMM+112
    movdqu [rsp+UNWIND_XMM+128],xmm14
    .savexmm128 xmm14,UNWIND_XMM+128
    movdqu [rsp+UNWIND_XMM+144],xmm15
    .savexmm128 xmm15,UNWIND_XMM+144
    .endprolog
    mov qword ptr [rsp+64],0
    mov rax,[rsp+SAVED+104]
    mov [rsp+72],rax
    mov rax,[rsp+SAVED+96]
    mov [rsp+80],rax
    mov rax,[rsp+SAVED+56]
    mov [rsp+88],rax
    mov rax,[rsp+SAVED+48]
    mov [rsp+96],rax
    lea rax,[rsp+FRAME_SIZE+120]
    mov [rsp+152],rax
    lea r10,NativeBridgeContexts
    add r10,n*32
    mov [rsp+168],r10
    mov r11,[r10]
    mov [rsp+176],r11
    mov ecx,[r10+16]
    xor edx,edx
copy_stack:
    cmp edx,ecx
    jae stack_done
    mov r11,[rax+40+rdx*8]
    mov [rsp+104+rdx*8],r11
    inc edx
    jmp copy_stack
stack_done:
    lea r11,[rsp+255]
    and r11,-64
    mov [rsp+160],r11
    xor eax,eax
    mov [r11+512],rax
    mov [r11+520],rax
    mov [r11+528],rax
    mov [r11+536],rax
    mov [r11+544],rax
    mov [r11+552],rax
    mov [r11+560],rax
    mov [r11+568],rax
    SAVE_XSTATE
    mov rcx,[rsp+168]
    lea rdx,[rsp+64]
    call ObserveBridge
    RESTORE_XSTATE
    test dword ptr [rsp+68],1
    jz call_native
    mov rax,[rsp+144]
    mov [rsp+SAVED+112],rax
    jmp finish
call_native:
    mov r10,[rsp+168]
    mov ecx,[r10+16]
    xor edx,edx
@@:
    cmp edx,ecx
    jae @F
    mov rax,[rsp+104+rdx*8]
    mov [rsp+32+rdx*8],rax
    inc edx
    jmp @B
@@:
    RESTORE_GP
    mov rcx,[rsp+72]
    mov rdx,[rsp+80]
    mov r8,[rsp+88]
    mov r9,[rsp+96]
    call qword ptr [rsp+176]
    SAVE_GP
    SAVE_UNWIND_XMM
    mov [rsp+136],rax
    mov qword ptr [rsp+64],1
    SAVE_XSTATE
    mov rcx,[rsp+168]
    lea rdx,[rsp+64]
    call ObserveBridge
    RESTORE_XSTATE
    test dword ptr [rsp+68],2
    jz no_override
    mov rax,[rsp+144]
    mov [rsp+SAVED+112],rax
no_override:
finish:
    ; A real Win64 epilogue must restore pushed registers with POPs. A single
    ; ADD of the entire frame allowed asynchronous unwinding to bypass the
    ; saved nonvolatile registers during GC/exception stack walks.
    add rsp,FRAME_SIZE
    pop r15
    pop r14
    pop r13
    pop r12
    pop r11
    pop r10
    pop r9
    pop r8
    pop rdi
    pop rsi
    pop rbp
    pop rbx
    pop rdx
    pop rcx
    pop rax
    ret
Bridge&n ENDP
ENDM
MAKE_BRIDGE 0
MAKE_BRIDGE 1
MAKE_BRIDGE 2
MAKE_BRIDGE 3
MAKE_BRIDGE 4
MAKE_BRIDGE 5
MAKE_BRIDGE 6
MAKE_BRIDGE 7
MAKE_BRIDGE 8
MAKE_BRIDGE 9
MAKE_BRIDGE 10
MAKE_BRIDGE 11
MAKE_BRIDGE 12
MAKE_BRIDGE 13
MAKE_BRIDGE 14
MAKE_BRIDGE 15
MAKE_BRIDGE 16
MAKE_BRIDGE 17
MAKE_BRIDGE 18
MAKE_BRIDGE 19
MAKE_BRIDGE 20
MAKE_BRIDGE 21
MAKE_BRIDGE 22
MAKE_BRIDGE 23
MAKE_BRIDGE 24
MAKE_BRIDGE 25
MAKE_BRIDGE 26
MAKE_BRIDGE 27
MAKE_BRIDGE 28
MAKE_BRIDGE 29
MAKE_BRIDGE 30
MAKE_BRIDGE 31
MAKE_BRIDGE 32
MAKE_BRIDGE 33
MAKE_BRIDGE 34
MAKE_BRIDGE 35
MAKE_BRIDGE 36
MAKE_BRIDGE 37
MAKE_BRIDGE 38
MAKE_BRIDGE 39
MAKE_BRIDGE 40
MAKE_BRIDGE 41
MAKE_BRIDGE 42
MAKE_BRIDGE 43
MAKE_BRIDGE 44
MAKE_BRIDGE 45
MAKE_BRIDGE 46
MAKE_BRIDGE 47
MAKE_BRIDGE 48
MAKE_BRIDGE 49
MAKE_BRIDGE 50
MAKE_BRIDGE 51
MAKE_BRIDGE 52
MAKE_BRIDGE 53
MAKE_BRIDGE 54
MAKE_BRIDGE 55
MAKE_BRIDGE 56
MAKE_BRIDGE 57
MAKE_BRIDGE 58
MAKE_BRIDGE 59
MAKE_BRIDGE 60
MAKE_BRIDGE 61
MAKE_BRIDGE 62
MAKE_BRIDGE 63
END

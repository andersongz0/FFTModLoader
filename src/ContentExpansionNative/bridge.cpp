#include <windows.h>
#include <intrin.h>
#include <cstdint>
#include <atomic>

struct BridgeContext {
    std::uint64_t original;
    std::uint64_t callback;
    std::uint32_t stackArguments;
    std::uint32_t reserved;
    std::uint64_t xsaveMask;
};
static_assert(sizeof(BridgeContext) == 32);
// Save observers are pulled by a CLR-created managed thread. Never call a
// reverse-P/Invoke delegate on a native-created worker for the save path.
// The owner waits, keeping source buffers alive and the capture synchronous.
static INIT_ONCE saveInit=INIT_ONCE_STATIC_INIT;
static SRWLOCK saveLock=SRWLOCK_INIT;
static HANDLE saveReady=nullptr,saveDone=nullptr;
static std::atomic<DWORD> saveThreadId{0};
static BridgeContext* saveContext=nullptr;
static void* saveFrame=nullptr;
// Gameplay observers also run on CLR-created threads. A native owner executes
// requested game helpers itself; nested detours are serviced by the waiting
// managed worker, without a reverse transition on a game stack. Owner context
// MUST be fiber-local: a helper can yield to another fiber on the same OS thread.
struct GameplayRequest {
    BridgeContext* context;
    void* frame;
    GameplayRequest* parent;
    GameplayRequest* next;
    GameplayRequest* child;
    HANDLE control,helperDone,childReady;
    std::atomic<unsigned> command{0};
    void* helper=nullptr;
    std::int64_t args[4]={};
    std::int64_t result=0;
};
static INIT_ONCE gameplayInit=INIT_ONCE_STATIC_INIT;
static SRWLOCK gameplayQueueLock=SRWLOCK_INIT;
static HANDLE gameplayReady=nullptr;
static DWORD ownerFiberKey=FLS_OUT_OF_INDEXES;
static GameplayRequest* gameplayFirst=nullptr;
static GameplayRequest* gameplayLast=nullptr;
static thread_local GameplayRequest* workerRequest=nullptr;
static BOOL CALLBACK InitializeGameplay(PINIT_ONCE,void*,void**) {
    ownerFiberKey=FlsAlloc(nullptr);
    gameplayReady=CreateSemaphoreW(nullptr,0,LONG_MAX,nullptr);
    return ownerFiberKey!=FLS_OUT_OF_INDEXES && gameplayReady?TRUE:FALSE;
}
static void ObserveGameplay(BridgeContext* context,void* frame) {
    if(!InitOnceExecuteOnce(&gameplayInit,InitializeGameplay,nullptr,nullptr))RaiseFailFastException(nullptr,nullptr,0);
    auto parent=static_cast<GameplayRequest*>(FlsGetValue(ownerFiberKey));
    GameplayRequest request{};request.context=context;request.frame=frame;request.parent=parent;
    request.control=CreateEventW(nullptr,FALSE,FALSE,nullptr);
    request.helperDone=CreateEventW(nullptr,FALSE,FALSE,nullptr);
    request.childReady=CreateEventW(nullptr,FALSE,FALSE,nullptr);
    if(!request.control || !request.helperDone || !request.childReady ||
       !FlsSetValue(ownerFiberKey,&request))RaiseFailFastException(nullptr,nullptr,0);
    if(parent) {parent->child=&request;SetEvent(parent->childReady);}
    else {
        AcquireSRWLockExclusive(&gameplayQueueLock);
        if(gameplayLast)gameplayLast->next=&request;else gameplayFirst=&request;
        gameplayLast=&request;
        ReleaseSRWLockExclusive(&gameplayQueueLock);
        ReleaseSemaphore(gameplayReady,1,nullptr);
    }
    for(;;) {
        if(WaitForSingleObject(request.control,INFINITE)!=WAIT_OBJECT_0)RaiseFailFastException(nullptr,nullptr,0);
        unsigned command=request.command.exchange(0);
        if(command==1)break;
        if(command!=2 || !request.helper)RaiseFailFastException(nullptr,nullptr,0);
        auto helper=reinterpret_cast<std::int64_t(*)(std::int64_t,std::int64_t,std::int64_t,std::int64_t)>(request.helper);
        request.result=helper(request.args[0],request.args[1],request.args[2],request.args[3]);
        SetEvent(request.helperDone);
    }
    FlsSetValue(ownerFiberKey,parent);
    CloseHandle(request.childReady);CloseHandle(request.helperDone);CloseHandle(request.control);
}
static BOOL CALLBACK InitializeSaveThread(PINIT_ONCE,void*,void**) {
    saveReady=CreateEventW(nullptr,FALSE,FALSE,nullptr);
    saveDone=CreateEventW(nullptr,FALSE,FALSE,nullptr);
    if(!saveReady || !saveDone)return FALSE;
    return TRUE;
}
extern "C" void ObserveBridge(BridgeContext* context,void* frame) {
    if(context->reserved==2) {ObserveGameplay(context,frame);return;}
    if(context->reserved==0) {
        reinterpret_cast<void(*)(void*)>(context->callback)(frame);return;
    }
    // Save observers must not invoke game helpers and reenter a save detour.
    // Fail fast on a programming error rather than deadlock or reintroduce
    // the native-worker reverse transition that v4 deliberately removes.
    if(GetCurrentThreadId()==saveThreadId.load())RaiseFailFastException(nullptr,nullptr,0);
    AcquireSRWLockExclusive(&saveLock);
    saveContext=context;saveFrame=frame;
    SetEvent(saveReady);
    if(WaitForSingleObject(saveDone,INFINITE)!=WAIT_OBJECT_0)RaiseFailFastException(nullptr,nullptr,0);
    saveContext=nullptr;saveFrame=nullptr;
    ReleaseSRWLockExclusive(&saveLock);
}
extern "C" {
__declspec(align(64)) BridgeContext NativeBridgeContexts[64] = {};
#define DECLARE(n) void Bridge##n();
DECLARE(0) DECLARE(1) DECLARE(2) DECLARE(3) DECLARE(4) DECLARE(5) DECLARE(6) DECLARE(7)
DECLARE(8) DECLARE(9) DECLARE(10) DECLARE(11) DECLARE(12) DECLARE(13) DECLARE(14) DECLARE(15)
DECLARE(16) DECLARE(17) DECLARE(18) DECLARE(19) DECLARE(20) DECLARE(21) DECLARE(22) DECLARE(23)
DECLARE(24) DECLARE(25) DECLARE(26) DECLARE(27) DECLARE(28) DECLARE(29) DECLARE(30) DECLARE(31)
DECLARE(32) DECLARE(33) DECLARE(34) DECLARE(35) DECLARE(36) DECLARE(37) DECLARE(38) DECLARE(39)
DECLARE(40) DECLARE(41) DECLARE(42) DECLARE(43) DECLARE(44) DECLARE(45) DECLARE(46) DECLARE(47)
DECLARE(48) DECLARE(49) DECLARE(50) DECLARE(51) DECLARE(52) DECLARE(53) DECLARE(54) DECLARE(55)
DECLARE(56) DECLARE(57) DECLARE(58) DECLARE(59) DECLARE(60) DECLARE(61) DECLARE(62) DECLARE(63)
#undef DECLARE
}
static void (*const bridges[64])() = {
#define ENTRY(n) Bridge##n,
ENTRY(0) ENTRY(1) ENTRY(2) ENTRY(3) ENTRY(4) ENTRY(5) ENTRY(6) ENTRY(7)
ENTRY(8) ENTRY(9) ENTRY(10) ENTRY(11) ENTRY(12) ENTRY(13) ENTRY(14) ENTRY(15)
ENTRY(16) ENTRY(17) ENTRY(18) ENTRY(19) ENTRY(20) ENTRY(21) ENTRY(22) ENTRY(23)
ENTRY(24) ENTRY(25) ENTRY(26) ENTRY(27) ENTRY(28) ENTRY(29) ENTRY(30) ENTRY(31)
ENTRY(32) ENTRY(33) ENTRY(34) ENTRY(35) ENTRY(36) ENTRY(37) ENTRY(38) ENTRY(39)
ENTRY(40) ENTRY(41) ENTRY(42) ENTRY(43) ENTRY(44) ENTRY(45) ENTRY(46) ENTRY(47)
ENTRY(48) ENTRY(49) ENTRY(50) ENTRY(51) ENTRY(52) ENTRY(53) ENTRY(54) ENTRY(55)
ENTRY(56) ENTRY(57) ENTRY(58) ENTRY(59) ENTRY(60) ENTRY(61) ENTRY(62) ENTRY(63)
#undef ENTRY
};
extern "C" __declspec(dllexport) void* GetBridgeAddress(unsigned index) {
    return index < 64 ? reinterpret_cast<void*>(bridges[index]) : nullptr;
}
// Publish once, before enabling a detour. Contexts remain valid for process lifetime.
extern "C" __declspec(dllexport) int ConfigureBridge(unsigned index, void* original,
                                                      void* callback, unsigned stackArguments) {
    if (index >= 64 || !original || !callback || stackArguments > 4 ||
        NativeBridgeContexts[index].original) return 0;
    int cpu[4];
    __cpuid(cpu, 1);
    if (!(cpu[2] & (1 << 27))) return 0;
    auto mask = _xgetbv(0);
    __cpuidex(cpu, 0xD, 0);
    if ((mask & 3) != 3 || cpu[1] < 576 || cpu[1] > 32768) return 0;
    NativeBridgeContexts[index] = {
        reinterpret_cast<std::uint64_t>(original), reinterpret_cast<std::uint64_t>(callback),
        stackArguments, 0, mask
    };
    return 1;
}
extern "C" __declspec(dllexport) int SetBridgeSaveDispatch(unsigned index) {
    if(index>=64 || !NativeBridgeContexts[index].original)return 0;
    if(!InitOnceExecuteOnce(&saveInit,InitializeSaveThread,nullptr,nullptr))return 0;
    NativeBridgeContexts[index].reserved=1;return 1;
}
extern "C" __declspec(dllexport) int WaitForSaveObserver(unsigned* index,void** frame) {
    if(!index || !frame || !InitOnceExecuteOnce(&saveInit,InitializeSaveThread,nullptr,nullptr))return 0;
    const DWORD current=GetCurrentThreadId();
    DWORD expected=0;
    if(!saveThreadId.compare_exchange_strong(expected,current) && expected!=current)return 0;
    if(WaitForSingleObject(saveReady,INFINITE)!=WAIT_OBJECT_0)return 0;
    *index=static_cast<unsigned>(saveContext-NativeBridgeContexts);*frame=saveFrame;return 1;
}
extern "C" __declspec(dllexport) int CompleteSaveObserver() {
    if(GetCurrentThreadId()!=saveThreadId.load() || !saveFrame)return 0;
    return SetEvent(saveDone)?1:0;
}
extern "C" __declspec(dllexport) int SetBridgeGameplayDispatch(unsigned index) {
    if(index>=64 || !NativeBridgeContexts[index].original ||
       !InitOnceExecuteOnce(&gameplayInit,InitializeGameplay,nullptr,nullptr))return 0;
    NativeBridgeContexts[index].reserved=2;return 1;
}
extern "C" __declspec(dllexport) int WaitForGameplayObserver(unsigned* index,void** frame,void** token) {
    if(!index || !frame || !token || workerRequest ||
       !InitOnceExecuteOnce(&gameplayInit,InitializeGameplay,nullptr,nullptr))return 0;
    if(WaitForSingleObject(gameplayReady,INFINITE)!=WAIT_OBJECT_0)return 0;
    AcquireSRWLockExclusive(&gameplayQueueLock);
    auto request=gameplayFirst;
    if(request) {gameplayFirst=request->next;if(!gameplayFirst)gameplayLast=nullptr;}
    ReleaseSRWLockExclusive(&gameplayQueueLock);
    if(!request)return 0;
    workerRequest=request;*index=static_cast<unsigned>(request->context-NativeBridgeContexts);
    *frame=request->frame;*token=request;return 1;
}
extern "C" __declspec(dllexport) int CompleteGameplayObserver(void* token) {
    auto request=static_cast<GameplayRequest*>(token);
    if(!request || request!=workerRequest)return 0;
    workerRequest=request->parent;
    request->command.store(1);
    // The native owner can destroy its request immediately after this signal.
    return SetEvent(request->control)?1:0;
}
extern "C" __declspec(dllexport) int BeginOwnerCall(void* helper,std::int64_t a,std::int64_t b,std::int64_t c,std::int64_t d) {
    auto request=workerRequest;
    if(!request || !helper || request->command.load()!=0)return 0;
    request->helper=helper;request->args[0]=a;request->args[1]=b;request->args[2]=c;request->args[3]=d;
    request->command.store(2);return SetEvent(request->control)?1:0;
}
extern "C" __declspec(dllexport) int WaitOwnerCall(std::int64_t* result,unsigned* index,void** frame,void** token) {
    auto request=workerRequest;
    if(!request || !result || !index || !frame || !token)return 0;
    HANDLE events[2]={request->helperDone,request->childReady};
    DWORD event=WaitForMultipleObjects(2,events,FALSE,INFINITE);
    if(event==WAIT_OBJECT_0){*result=request->result;return 1;}
    if(event!=WAIT_OBJECT_0+1 || !request->child)return 0;
    auto child=request->child;request->child=nullptr;
    workerRequest=child;*index=static_cast<unsigned>(child->context-NativeBridgeContexts);
    *frame=child->frame;*token=child;return 2;
}
extern "C" __declspec(dllexport) unsigned GetBridgeVersion() { return 5; }

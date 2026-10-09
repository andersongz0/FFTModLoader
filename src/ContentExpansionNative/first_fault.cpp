// Bounded first-chance evidence only. No CLR calls, game memory writes,
// exception suppression or full heap dumps. Include pointed memory/write audit.
#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
namespace {
constexpr unsigned Capacity=16,CodeSize=512,StackSize=16384;
struct LegacySnapshot {
 char magic[8];std::uint32_t version,size,thread,codeBytes,stackBytes;
 std::uint64_t codeAddress,stackAddress;
 EXCEPTION_RECORD exception;CONTEXT context;
 wchar_t module[MAX_PATH];unsigned char code[CodeSize],stack[StackSize];
};
constexpr unsigned SampleCount=10,SampleSize=2048,WriteCapacity=1024;
struct MemorySample {std::uint64_t address;std::uint32_t bytes,reserved;unsigned char data[SampleSize];};
struct WriteRecord {
 volatile LONG64 sequence;std::uint64_t address;std::uint32_t length,thread;
 unsigned char data[16];std::uint32_t phase,reserved;
};
static_assert(sizeof(WriteRecord)==48);
struct Snapshot {LegacySnapshot base;MemorySample memory[SampleCount];LONG64 writeCount;WriteRecord writes[WriteCapacity];};
WriteRecord audit[WriteCapacity]={};volatile LONG64 writeCount=0;
HANDLE files[Capacity]={};Snapshot snapshots[Capacity]={};
volatile LONG next=-1,enabled=0;PVOID handler=nullptr;
unsigned SafeRead(void* output,std::uintptr_t address,unsigned wanted) {
 MEMORY_BASIC_INFORMATION info={};
 if(!VirtualQuery(reinterpret_cast<void*>(address),&info,sizeof(info)) || info.State!=MEM_COMMIT ||
    (info.Protect&(PAGE_NOACCESS|PAGE_GUARD)))return 0;
 auto end=reinterpret_cast<std::uintptr_t>(info.BaseAddress)+info.RegionSize;
 unsigned count=static_cast<unsigned>((end-address)<wanted?(end-address):wanted);
 __try {memcpy(output,reinterpret_cast<void*>(address),count);return count;}
 __except(EXCEPTION_EXECUTE_HANDLER){return 0;}
}
LONG CALLBACK FirstFault(PEXCEPTION_POINTERS p) {
 if(!enabled || p->ExceptionRecord->ExceptionCode!=EXCEPTION_ACCESS_VIOLATION ||
    p->ExceptionRecord->NumberParameters<2)return EXCEPTION_CONTINUE_SEARCH;
 LONG index=InterlockedIncrement(&next);
 if(index<0 || index>=static_cast<LONG>(Capacity))return EXCEPTION_CONTINUE_SEARCH;
 auto& snapshot=snapshots[index];auto& s=snapshot.base;
 memcpy(s.magic,"CEXFAULT",8);s.version=2;s.size=sizeof(snapshot);s.thread=GetCurrentThreadId();
 s.exception=*p->ExceptionRecord;s.context=*p->ContextRecord;
 auto ip=static_cast<std::uintptr_t>(s.context.Rip);
 s.codeAddress=ip>=128?ip-128:ip;
 MEMORY_BASIC_INFORMATION codeRegion={};
 if(VirtualQuery(reinterpret_cast<void*>(ip),&codeRegion,sizeof(codeRegion))) {
  auto begin=reinterpret_cast<std::uintptr_t>(codeRegion.BaseAddress);
  if(s.codeAddress<begin)s.codeAddress=begin;
 }
 s.codeBytes=SafeRead(s.code,static_cast<std::uintptr_t>(s.codeAddress),CodeSize);
 s.stackAddress=s.context.Rsp;
 s.stackBytes=SafeRead(s.stack,static_cast<std::uintptr_t>(s.stackAddress),StackSize);
 // Capture only these bounded pointed ranges, never scan the heap. Masking a
 // noncanonical pointer is diagnostic only: it is NOT repaired or used by game.
 std::uintptr_t addresses[SampleCount]={s.context.R13,s.context.R15,
  s.context.R15&0x0000FFFFFFFFFFFFULL,s.context.R11,s.context.Rcx,s.context.Rdx,
  s.context.Rax,s.context.Rdi,s.context.Rsi,s.context.Rbp};
 for(unsigned i=0;i<SampleCount;++i){
  auto& sample=snapshot.memory[i];sample.address=addresses[i];
  sample.bytes=SafeRead(sample.data,addresses[i],SampleSize);
 }
 snapshot.writeCount=InterlockedCompareExchange64(&writeCount,0,0);
 for(unsigned i=0;i<WriteCapacity;++i){
  LONG64 before=InterlockedCompareExchange64(&audit[i].sequence,0,0);
  if(before<=0)continue;
  memcpy(&snapshot.writes[i],&audit[i],sizeof(WriteRecord));
  if(before!=InterlockedCompareExchange64(&audit[i].sequence,0,0))snapshot.writes[i].sequence=0;
 }
 HMODULE module=nullptr;
 if(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                      reinterpret_cast<LPCWSTR>(ip),&module))GetModuleFileNameW(module,s.module,MAX_PATH);
 DWORD written=0;
 if(files[index] && files[index]!=INVALID_HANDLE_VALUE){
  WriteFile(files[index],&snapshot,sizeof(snapshot),&written,nullptr);FlushFileBuffers(files[index]);
 }
 // Windows and CLR still receive exactly the original exception.
 return EXCEPTION_CONTINUE_SEARCH;
}
}
extern "C" __declspec(dllexport) unsigned GetFirstFaultSnapshotSize(){return sizeof(Snapshot);}
// Fixed ring, no allocation/CLR/locks on the write path. Observe writes from
// this mod only; it does not attribute writes made by native game/other mods.
extern "C" __declspec(dllexport) void RecordChemistWrite(std::uintptr_t address,unsigned length,unsigned phase){
 if(!enabled || !length)return;
 LONG64 ticket=InterlockedIncrement64(&writeCount);
 auto& record=audit[(ticket-1)%WriteCapacity];
 // A busy ring slot is skipped, never overwritten concurrently. Tickets may
 // therefore have gaps. Exception snapshots discard any changing/busy slot.
 LONG64 previous=InterlockedCompareExchange64(&record.sequence,0,0);
 if(previous<0 || InterlockedCompareExchange64(&record.sequence,-ticket,previous)!=previous)return;
 record.address=address;record.length=length;record.thread=GetCurrentThreadId();record.phase=phase;
 memset(record.data,0,sizeof(record.data));SafeRead(record.data,address,length<16?length:16);
 InterlockedExchange64(&record.sequence,ticket);
}
extern "C" __declspec(dllexport) int EnableFirstFaultCapture(const wchar_t* directory) {
 if(!directory || InterlockedCompareExchange(&enabled,0,0)!=0)return 0;
 wchar_t path[32768]={};
 for(unsigned i=0;i<Capacity;++i){
  int length=swprintf_s(path,L"%s\\first-fault-%lu-%02u.bin",directory,GetCurrentProcessId(),i);
  if(length<0)return 0;
  files[i]=CreateFileW(path,GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);
  if(files[i]==INVALID_HANDLE_VALUE){
   for(unsigned j=0;j<i;++j)CloseHandle(files[j]);return 0;
  }
 }
 handler=AddVectoredExceptionHandler(1,FirstFault);
 if(!handler){for(auto file:files)CloseHandle(file);return 0;}
 InterlockedExchange(&enabled,1);return 1;
}

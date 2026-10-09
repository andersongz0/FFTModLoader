using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>Scoped X-only positioning of the existing Enhanced list reference.
/// Never clone scene nodes, copy text, alter input or resize a list.</summary>
internal sealed class NativeMedicineMenuPosition
{
    internal static readonly int[] Helpers=[0x3F9DA0,0x3D87BC,0x3D8D1C,0x3F0794,0x3EF684];
    readonly long _image,_name;
    readonly Func<int,long,long,long,long,long> _call;
    readonly Action<string> _log;
    int _reports;
    internal NativeMedicineMenuPosition(long image,long name,Func<int,long,long,long,long,long> call,Action<string> log)
    {_image=image;_name=name;_call=call;_log=log;}
    static long Ptr(long p)=>BinaryPrimitives.ReadInt64LittleEndian(CheckedNativeRead.Read(p,8));
    static int Int(long p)=>BinaryPrimitives.ReadInt32LittleEndian(CheckedNativeRead.Read(p,4));
    internal static int Inset(int width)
    {if(width is <160 or >1600)throw new InvalidDataException("Unexpected medicine list width.");return (width+2)/4;}
    internal IDisposable? Begin()
    {
        try{return BeginChecked();}
        catch(Exception ex){if(Interlocked.Increment(ref _reports)<=12)_log("[Medicine/Posição] layout nativo preservado: "+ex.Message);return null;}
    }
    unsafe long Named(long root,long name)
    {
        byte* hash=stackalloc byte[16];new Span<byte>(hash,16).Clear();
        _call(0x3D87BC,(long)hash,name,0,0);
        try{return _call(0x3F0794,root+0x110,(long)hash,0,0);}
        finally{int token=*(int*)(hash+8);if(token!=0)_call(0x3D8D1C,Ptr(_image+0x3CDA2A0),token,0,0);}
    }
    IDisposable? BeginChecked()
    {
        long provider=Ptr(_image+0x3CD9DA8);if(provider==0)return null;
        long manager=Ptr(provider+0x10);if(manager==0)return null;
        long battle=Ptr(manager+0x40);if(battle==0)return null;
        long owner=Ptr(battle+0x48);if(owner==0)return null;
        long resource=Ptr(owner+0x18),component=Ptr(owner+0x48);
        if(resource==0||component==0)return null;
        long root=_call(0x3F9DA0,resource,0,0,0);if(root==0)return null;
        long node=Named(root,_name);
        if(node==0||Int(node+0x34)!=12)return null;
        int width=Int(node+0x40);if(width is <160 or >1600)width=Int(component+0x150);
        int step=Inset(width),x=Int(node+0x38),y=Int(node+0x3C);
        int viewport=Int(root+0x150),overflow=viewport is >=640 and <=8192?Math.Max(0,x+step+width-viewport):0;
        long menu=0;int menuX=0,menuY=0;
        if(overflow>0)
        {
            menu=Named(root,_name+16);
            if(menu==0||menu==node||Int(menu+0x34)!=12)return null;
            menuX=Int(menu+0x38);menuY=Int(menu+0x3C);
            if(menuX-overflow<0)return null;
        }
        // Keep exactly the existing native reference, its full hierarchy and
        // screen scale. The native setter also invalidates cached coordinates.
        if(menu!=0)_call(0x3EF684,menu,menuX-overflow,menuY,0);
        _call(0x3EF684,node,checked(x+step-overflow),y,0);
        if(Interlocked.Increment(ref _reports)<=12)_log($"[Medicine/Posição] submenu X={x}+{step}; largura={width}; deslocamento=1/4; sem cópia de texto ou janela.");
        return new Scope(this,provider,owner,resource,component,node,x,y,menu,menuX,menuY);
    }
    sealed class Scope(NativeMedicineMenuPosition position,long provider,long owner,long resource,long component,long node,int x,int y,long menu,int menuX,int menuY):IDisposable
    {
        bool restored;
        public void Dispose()
        {
            if(restored)return;restored=true;
            // Never dereference a stale scene pointer after a generation change.
            if(Ptr(position._image+0x3CD9DA8)!=provider)return;
            long manager=Ptr(provider+0x10);if(manager==0)return;
            long battle=Ptr(manager+0x40);if(battle==0||Ptr(battle+0x48)!=owner)return;
            if(Ptr(owner+0x18)!=resource||Ptr(owner+0x48)!=component)return;
            position._call(0x3EF684,node,x,y,0);
            if(menu!=0)position._call(0x3EF684,menu,menuX,menuY,0);
        }
    }
}

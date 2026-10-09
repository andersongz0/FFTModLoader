using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>Presentation-only snapshot of Items behind the native modal list.
/// No cloned GUI components, input handlers, timelines or inventory storage.
/// Drawing nodes belong to the native GUI resource and are reused until it exits.
/// </summary>
internal sealed unsafe class NativeMedicineMenuCascade
{
    internal static readonly int[] Helpers=[0x3F9DA0,0x3D87BC,0x3D8D1C,0x3F0794,0x3D87F4,
        0x0FE810,0x3EF54C,0x3FAD88,0x3F0A38,0x3F0E24,0x3F0654,0x3F9938,0x3EF684];
    private readonly long _image,_names;
    private readonly Func<int,long,long,long,long,long> _call;
    private readonly Action<string> _log;
    private long _resource,_root,_wrapper,_title;
    private bool _nameReady;
    private int _strip;
    private readonly Dictionary<int,List<long>> _pool=[];
    private readonly Dictionary<int,int> _used=[];
    private int _reports;
    private record Node(long Source,int Kind,List<Node> Children);
    internal NativeMedicineMenuCascade(long image,long names,Func<int,long,long,long,long,long> call,Action<string> log)
    {_image=image;_names=names;_call=call;_log=log;}
    private static byte[] Read(long p,int n)=>CheckedNativeRead.Read(p,n);
    private static long Ptr(long p)=>BinaryPrimitives.ReadInt64LittleEndian(Read(p,8));
    private static int Int(long p)=>BinaryPrimitives.ReadInt32LittleEndian(Read(p,4));
    private static void Write(long p,byte[] bytes)=>NativeLifetimeMemory.WriteProtected(p,bytes);
    private long Call(int r,long a=0,long b=0,long c=0,long d=0)=>_call(r,a,b,c,d);
    // The approved screenshot's native cascade leaves ~88 of a ~293-pixel
    // panel visible (30%). Apply that proportion to the real design width;
    // do not hard-code physical pixels or change the game's screen scale.
    internal static int Inset(int width)
    {if(width is <160 or >1600)throw new InvalidDataException("Unexpected medicine list width.");return (width*3+5)/10;}
    private long Named(long component,long name)
    {
        byte* hash=stackalloc byte[16];new Span<byte>(hash,16).Clear();
        Call(0x3D87BC,(long)hash,name);
        try{return Call(0x3F0794,component+0x110,(long)hash);}
        finally{int token=*(int*)(hash+8);if(token!=0)Call(0x3D8D1C,Ptr(_image+0x3CDA2A0),token);}
    }
    private Node? Plan(long p,int depth,HashSet<long> seen,ref int budget)
    {
        if(depth>20||--budget<0||!seen.Add(p))throw new InvalidDataException("Unbounded medicine drawing tree.");
        int kind=Int(p+0x34);List<Node> children=[];
        if(kind is 8 or 10 or 11 or 13)return null; // no particles, models or collision/input in passive copy
        // Parent row text can sort above the active child independently of its
        // containing layer. Keep only the short Items header in the exposed strip.
        if(kind==3&&p!=_title)return null;
        if(kind==2&&(Int(p+0x38)>=_strip||Int(p+0x40)>_strip))return null;
        if(kind==1)
        {
            for(long child=Ptr(p+0xD0);child!=0;child=Ptr(child+0x20))
            {var drawing=Plan(child,depth+1,seen,ref budget);if(drawing!=null)children.Add(drawing);}
        }
        else if(kind is 9 or 12)
        {
            long component=kind==12?Call(0x3F9938,_resource,p+0xD0,p+0xE0,p+0x130):Call(0x3F9938,_resource,p+0xF8,p+0x108,p+0x120);
            if(component!=0){var drawing=Plan(component+0x110,depth+1,seen,ref budget);if(drawing!=null)children.Add(drawing);}
        }
        else if(kind is not (2 or 3 or 4 or 6 or 7))
            throw new InvalidDataException("Unsupported medicine drawing node "+kind);
        return new(p,kind,children);
    }
    private void ResetPool()
    {
        _used.Clear();Write(_wrapper+0x90,[0]);Write(_wrapper+0xD0,new byte[8]);
        foreach(var entry in _pool)
        foreach(long p in entry.Value)
        {
            Write(p+0x18,new byte[24]);Write(p+0x90,[0]);
            if(entry.Key==1)Write(p+0xD0,new byte[8]);
        }
    }
    private long Draw(Node n)
    {
        int kind=n.Kind is 9 or 12?1:n.Kind;
        if(!_pool.TryGetValue(kind,out var nodes))_pool.Add(kind,nodes=[]);
        int index=_used.GetValueOrDefault(kind);_used[kind]=index+1;long p;
        if(index<nodes.Count)
        {
            p=nodes[index];Call(kind==2?0x3F0A38:kind==3?0x3F0E24:0x3EF54C,p,n.Source);
            // The menu's nine-grid/reference decorations do not change within
            // a resource; their type-specific atlas properties stay native.
        }
        else
        {
            if(_pool.Values.Sum(v=>v.Count)>=768)throw new InvalidDataException("Medicine drawing cache is full.");
            p=kind==1?Call(0x0FE810,_resource,Ptr(n.Source+8)):Call(0x3FAD88,_resource,n.Source,0);
            if(p==0)throw new InvalidDataException("Native medicine drawing allocation failed.");
            nodes.Add(p);if(kind==1)Call(0x3EF54C,p,n.Source);
        }
        // Never use a null CString: native 3F0794 compares names byte-by-byte
        // when either hash token is zero, even on hidden nodes. A reserved valid
        // name isolates passive nodes without breaking later native lookups.
        Call(0x3D87F4,p+8,_names+96);
        if(kind is 3 or 4 or 6)Write(p+0x40,BitConverter.GetBytes(Math.Min(Int(p+0x40),_strip)));
        if(kind==1)
        {
            long previous=0;
            foreach(Node child in n.Children){long c=Draw(child);Call(0x3F0654,p,c,previous);previous=c;}
        }
        return p;
    }
    internal IDisposable? Begin()
    {
        long actionMenu=0;int originalX=0,originalY=0;bool moved=false;
        try
        {
            long provider=Ptr(_image+0x3CD9DA8);if(provider==0)return null;
            long manager=Ptr(provider+0x10),battle=Ptr(manager+0x40),owner=Ptr(battle+0x48);
            if(owner==0)return null;
            long resource=Ptr(owner+0x18),root=Call(0x3F9DA0,resource);
            if(root==0)return null;
            long items=Named(root,_names),menu=Named(root,_names+32),list=Named(Ptr(owner+0x48),_names+64);
            if(items==0||menu==0||list==0||Int(items+0x34)!=12||Int(menu+0x34)!=12)return null;
            int step=Inset(Int(list+0x40));_strip=step;
            if(!_nameReady){Call(0x3D87BC,_names+96,_names+112);_nameReady=true;}
            _title=Named(Ptr(owner+0x48),_names+144);
            if(resource!=_resource||root!=_root)
            {
                // Do not touch drawing pointers owned by a departed scene.
                _pool.Clear();_used.Clear();_resource=resource;_root=root;
                _wrapper=0;
                _wrapper=Call(0x0FE810,resource,_names+112);
                if(_wrapper==0)throw new InvalidDataException("Native medicine parent allocation failed.");
                Call(0x3D87F4,_wrapper+8,_names+96);
                Write(_wrapper+0x90,[0]);Call(0x3F0654,root+0x110,_wrapper,items);
            }
            ResetPool();int budget=768;
            Node plan=Plan(Ptr(owner+0x48)+0x110,0,[],ref budget)??throw new InvalidDataException("Missing medicine parent drawing.");
            long drawing=Draw(plan);Call(0x3EF54C,_wrapper,items);Call(0x3D87F4,_wrapper+8,_names+96);
            Call(0x3F0654,_wrapper,drawing,0);
            Call(0x3EF684,_wrapper,Int(items+0x38)-step,Int(items+0x3C));
            // Passive parent is behind the active child and has no key/mouse
            // collision nodes. Both prior menus move left one native inset so
            // the fourth level remains inside the existing right screen edge.
            Write(_wrapper+0x5C,BitConverter.GetBytes(Int(items+0x5C)-1));
            Write(_wrapper+0x90,[1]);Write(drawing+0x90,[1]);
            actionMenu=menu;originalX=Int(menu+0x38);originalY=Int(menu+0x3C);
            Call(0x3EF684,menu,originalX-step,originalY);moved=true;
            if(_reports++<8)_log($"[Medicine/Cascata] faixa={step}; título/ícones sem texto das linhas; nomes nativos válidos; largura={Int(list+0x40)}.");
            return new Scope(()=>{Write(_wrapper+0x90,[0]);Call(0x3EF684,menu,originalX,originalY);});
        }
        catch(Exception ex)
        {
            if(_wrapper!=0&&_root!=0)Write(_wrapper+0x90,[0]);
            if(moved)Call(0x3EF684,actionMenu,originalX,originalY);
            if(_reports++<8)_log("[Medicine/Cascata] disposição anterior preservada: "+ex.Message);
            return null;
        }
    }
    private sealed class Scope(Action restore):IDisposable
    {private Action? _restore=restore;public void Dispose()=>Interlocked.Exchange(ref _restore,null)?.Invoke();}
}

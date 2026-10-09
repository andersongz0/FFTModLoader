using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>A separate native component/reference for the fourth menu level.
/// The real Items tree remains attached and is never flattened or copied into
/// drawing strips. Only the existing modal controller is scoped to the child.</summary>
internal sealed unsafe class NativeMedicineFourthMenu
{
    internal static readonly int[] Helpers=[0x3F9DA0,0x3D87BC,0x3D8D1C,0x3D87F4,
        0x3F0794,0x3FAD88,0x3F9938,0x3F0654,0x3EF684,0x4091AC,0x40AAC0,0x3F0F8C,
        0x408EBC,0x408FF8,0x409ECC,0x409824,0x1568EC];
    readonly long _image,_names;
    readonly Func<int,long,long,long,long,long> _call;
    readonly Action<string> _log;
    long _resource,_root,_parent,_child,_component;
    bool _constructionFailed;
    int _reports;
    internal NativeMedicineFourthMenu(long image,long names,Func<int,long,long,long,long,long> call,Action<string> log)
    {_image=image;_names=names;_call=call;_log=log;}
    static long Ptr(long p)=>BinaryPrimitives.ReadInt64LittleEndian(CheckedNativeRead.Read(p,8));
    static int Int(long p)=>BinaryPrimitives.ReadInt32LittleEndian(CheckedNativeRead.Read(p,4));
    static void Put(long p,long v)=>NativeLifetimeMemory.WriteProtected(p,BitConverter.GetBytes(v));
    static void Number(long p,int v)=>NativeLifetimeMemory.WriteProtected(p,BitConverter.GetBytes(v));
    static void Byte(long p,byte v)=>NativeLifetimeMemory.WriteProtected(p,[v]);
    static ushort Group(long p)=>BinaryPrimitives.ReadUInt16LittleEndian(CheckedNativeRead.Read(p+0x228,2));
    void IsolateRenderGroups(long resource,long first,long count)
    {
        // Native408C4C copies +228 verbatim. Native3EDCA4 turns this into
        // context.+224, which3E9298 uses to reuse old batches and3E75DC
        // uses to reorder them. Independent GUI IDs do NOT isolate drawing.
        // Remap only the freshly allocated tree; preserve shared groups inside
        // that tree, zero/inherited groups and every original component.
        long vector=Ptr(resource+0x68);
        HashSet<ushort> used=[];
        for(long i=0;i<count;i++)used.Add(Group(Ptr(vector+i*8)));
        Dictionary<ushort,ushort> remap=[];
        ushort Allocate()
        {
            // Allocate from the high end, away from normal UIB low-numbered
            // groups; also reject every group already present in this resource.
            for(int candidate=ushort.MaxValue;candidate>0;candidate--)
                if(used.Add((ushort)candidate))return (ushort)candidate;
            throw new InvalidDataException("No independent native render group available.");
        }
        // Plan before writing, so exhaustion cannot leave half a remap.
        List<(long Component,ushort Group)> plan=[];
        for(long i=first;i<count;i++)
        {
            long c=Ptr(vector+i*8);ushort old=Group(c);
            if(old==0&&c!=_component)continue;
            if(!remap.TryGetValue(old,out ushort fresh)){fresh=Allocate();remap.Add(old,fresh);}
            plan.Add((c,fresh));
        }
        foreach(var (c,fresh) in plan)NativeLifetimeMemory.WriteProtected(c+0x228,BitConverter.GetBytes(fresh));
        // The top-level child is a separate native batch boundary, even when
        // it has the same material state as the still-visible parent.
        Byte(_component+0x231,1);
    }
    long Named(long component,int name,bool resolve=false)
    {
        byte* hash=stackalloc byte[16];new Span<byte>(hash,16).Clear();
        _call(0x3D87BC,(long)hash,_names+name,0,0);
        try{return _call(resolve?0x4091AC:0x3F0794,resolve?component:component+0x110,(long)hash,0,0);}
        finally{int token=*(int*)(hash+8);if(token!=0)_call(0x3D8D1C,Ptr(_image+0x3CDA2A0),token,0,0);}
    }
    void Rename(long node,int name)
    {
        byte* hash=stackalloc byte[16];new Span<byte>(hash,16).Clear();
        _call(0x3D87BC,(long)hash,_names+name,0,0);
        try{_call(0x3D87F4,node+8,(long)hash,0,0);}
        finally{int token=*(int*)(hash+8);if(token!=0)_call(0x3D8D1C,Ptr(_image+0x3CDA2A0),token,0,0);}
    }
    void Timeline(long component,int name,int helper,long frame=0)
    {
        byte* hash=stackalloc byte[16];new Span<byte>(hash,16).Clear();
        _call(0x3D87BC,(long)hash,_names+name,0,0);
        try{_call(helper,component,(long)hash,frame,0);}
        finally{int token=*(int*)(hash+8);if(token!=0)_call(0x3D8D1C,Ptr(_image+0x3CDA2A0),token,0,0);}
    }
    void ShowItems(long parent)
    {
        // A visible reference alone does not cancel the native close animation.
        // Use the same component enable/Show/Hide timeline operations as native
        // menus, without opening another input controller or rewriting its rows.
        Timeline(parent,176,0x409ECC);
        _call(0x408EBC,parent,1,0,0);
        Timeline(parent,160,0x409824,-1);
        Byte(parent+0x1A0,1);
    }
    internal Scope? Begin(int group)
    {
        if(group is <0 or >2)throw new ArgumentOutOfRangeException(nameof(group));
        Scope? scope=null;
        try
        {
            long provider=Ptr(_image+0x3CD9DA8);if(provider==0)return null;
            long manager=Ptr(provider+0x10);if(manager==0)return null;
            long battle=Ptr(manager+0x40);if(battle==0)return null;
            long owner=Ptr(battle+0x48);if(owner==0)return null;
            long resource=Ptr(owner+0x18),parent=Ptr(owner+0x48);if(resource==0||parent==0)return null;
            long root=_call(0x3F9DA0,resource,0,0,0);if(root==0)return null;
            long reference=Named(root,0),menu=Named(root,16);
            if(reference==0||menu==0||Int(reference+0x34)!=12||Int(menu+0x34)!=12)return null;
            // Live GUI: ActionList lives under Widget/Layer00, not directly
            // under Widget. 3F0654 assigns parent.+18 from its first argument
            // but inserts next/previous after its third argument regardless.
            // Mixing the root layer with a nested anchor corrupts ownership
            // and dirties the wrong sibling list, leaving Items drawn on top.
            long layer=Ptr(reference+0x18);
            if(layer==0||Int(layer+0x34)!=1)return null;
            int width=Int(reference+0x40);if(width is <160 or >1600)width=Int(parent+0x150);
            int step=NativeMedicineMenuPosition.Inset(width);
            if(Int(reference+0x38)<step||Int(menu+0x38)<step)return null;
            if(_resource!=resource||_root!=root||_parent!=parent)
            {
                // Resource owns allocation/destruction. Never dereference an
                // old generation; never register a second scheduler or fiber.
                _resource=resource;_root=root;_parent=parent;_child=0;_component=0;_constructionFailed=false;
            }
            if(_constructionFailed)return null; // never retry partial allocations in this resource
            // The allocator may reuse all resource addresses after scene exit.
            // Verify membership using the current root before touching cache.
            if(_child!=0&&Named(root,32)!=_child){_child=0;_component=0;}
            if(_child==0)
            {
                long begin=Ptr(resource+0x68),end=Ptr(resource+0x70);long count=(end-begin)/8;
                if(begin==0||end<=begin||(end-begin)%8!=0||count is <1 or >4096)throw new InvalidDataException("Unexpected native GUI resource size.");
                // Native reference copy recursively creates independent real
                // components, list rows, text, atlas references and timelines.
                // This is the same constructor used by native list expansion.
                _child=_call(0x3FAD88,resource,reference,0,0);
                if(_child==0||Int(_child+0x34)!=12)throw new InvalidDataException("Fourth native reference was not created.");
                Byte(_child+0x90,0);Rename(_child,32);
                _component=_call(0x3F9938,resource,_child+0xD0,_child+0xE0,_child+0x130);
                if(_component==0||_component==parent)throw new InvalidDataException("Fourth list still aliases Items.");
                long after=(Ptr(resource+0x70)-Ptr(resource+0x68))/8;
                if(after-count is <1 or >768)throw new InvalidDataException("Unbounded fourth menu construction.");
                for(long i=count;i<after;i++)
                {
                    long c=Ptr(Ptr(resource+0x68)+i*8),function=Ptr(Ptr(c)+0x18);
                    _call(checked((int)(function-_image)),c,0,0,0);
                }
                IsolateRenderGroups(resource,count,after);
                _call(0x3F0654,layer,_child,reference,0);
            }
            if(Ptr(_child+0x18)!=layer)throw new InvalidDataException("Fourth menu is not a sibling of Items.");
            long list=Named(_component,64,true),titleBoard=Named(_component,80,true);
            if(list==0||Int(list+0x218)!=9||titleBoard==0||Int(titleBoard+0x218)!=13)
                throw new InvalidDataException("Fourth list/title native bindings absent.");
            long title=_call(0x40AAC0,titleBoard,0,0,0);
            if(title==0||Int(title+0x34)!=3)throw new InvalidDataException("Fourth title is not a native text node.");
            // Same owner subscriber as native ActionList initialization. Parent
            // mouse notifications are ignored by the owner's component check;
            // keyboard, stock and target ownership remain the approved modal.
            NativeLifetimeMemory.WriteProtected(list+0x248,new byte[16]);Number(list+0x248,Int(owner+8));
            scope=new(this,provider,owner,resource,root,parent,reference,menu,_child,_component,
                Int(reference+0x38),Int(reference+0x3C),Int(menu+0x38),Int(menu+0x3C),Ptr(owner+0x1590),
                CheckedNativeRead.Read(reference+0x90,1)[0],CheckedNativeRead.Read(parent+0x1A0,1)[0],
                CheckedNativeRead.Read(parent+0x22A,1)[0]);
            // ActionMenu is Abilities, NOT the character's menu. Move the
            // BattleMenu references with it, retaining the native spacing.
            for(int i=0;i<6;i++)
            {
                long character=Named(root,192+i*16);
                if(character!=0&&Int(character+0x34)==12)
                    scope.Characters.Add((character,Int(character+0x38),Int(character+0x3C)));
            }
            long core=Ptr(_image+0x3CD9DC0);
            if(core!=0){scope.Core=core;scope.CoreId=Int(core+0xADDC);Number(core+0xADDC,Int(_component+8));}
            // Preserve both earlier menu levels and the full original Items.
            // The child retains the original right edge, while the preceding
            // menus move one inset left, as the standard cascade does.
            _call(0x3EF684,reference,scope.X-step,scope.Y,0);
            _call(0x3EF684,menu,scope.MenuX-step,scope.MenuY,0);
            foreach(var (node,cx,cy) in scope.Characters)_call(0x3EF684,node,cx-step,cy,0);
            _call(0x3EF684,_child,scope.X,scope.Y,0);
            // 4092EC sorts UPDATE siblings descending. Actual rendering in
            // 3E9A6C walks tail-to-head, with the accumulated +C8 priority
            // calculated by 3EFF08, then inherited through 3EDCA4/+220.
            // A direct +5C write alone leaves that cached +C8 unchanged when
            // the copied reference is already at the requested position.
            // Invalidate the transform/depth even on unchanged-X reopens.
            Number(_child+0x5C,checked(Int(reference+0x5C)+1));
            Byte(_child+0x95,1);Byte(layer+0xD8,1);
            // Items' title gadget must not be repurposed by the child. Native
            // UTF-8 text storage owns the explicit family title independently.
            Put(owner+0x1590,0);Number(title+0xF8,0);_call(0x3F0F8C,title,_names+112+group*16,0,0);
            Put(owner+0x48,_component);Byte(reference+0x90,1);Byte(parent+0x1A0,1);
            ShowItems(parent);
            Byte(_component+0x1A0,1);Byte(_child+0x90,1);
            if(_reports++<12)_log($"[Medicine/4 menus] Camada de Items compartilhada={Ptr(_child+0x18)==Ptr(reference+0x18)}; camada intermediária={layer!=root+0x110}; ordenação da camada invalidada={Int(layer+0x34)==1&&CheckedNativeRead.Read(layer+0xD8,1)[0]==1}; família={group}; render Items={Group(parent)}, submenu={Group(_component)}; prioridade Items={Int(reference+0x5C)}, submenu={Int(_child+0x5C)}, cache anterior={Int(_child+0xC8)}; passo={step}; menus de personagem deslocados={scope.Characters.Count}; somente seleção do submenu ativa.");
            scope.CloseItemsOnDispose=true;
            return scope;
        }
        catch(Exception ex)
        {
            scope?.Dispose();
            _constructionFailed=true;
            if(_reports++<12)_log("[Medicine/4 menus] construção indisponível; menu funcional anterior preservado: "+ex.Message);
            return null;
        }
    }
    internal sealed class Scope(NativeMedicineFourthMenu menus,long provider,long owner,long resource,long root,long parent,long reference,long menu,long child,long component,
        int x,int y,int menuX,int menuY,long titleBinding,byte referenceVisible,byte parentVisible,byte parentShown):IDisposable
    {
        internal int X=x,Y=y,MenuX=menuX,MenuY=menuY,CoreId;
        internal long Core;
        internal bool CloseItemsOnDispose;
        internal readonly List<(long Node,int X,int Y)> Characters=[];
        bool disposed,returning;
        bool Current(bool allowChild)
        {
            if(Ptr(menus._image+0x3CD9DA8)!=provider)return false;
            long manager=Ptr(provider+0x10);if(manager==0)return false;
            long battle=Ptr(manager+0x40);if(battle==0||Ptr(battle+0x48)!=owner||Ptr(owner+0x18)!=resource)return false;
            if(menus._call(0x3F9DA0,resource,0,0,0)!=root||menus.Named(root,32)!=child)return false;
            return Ptr(owner+0x48)==parent||(allowChild&&Ptr(owner+0x48)==component);
        }
        void CloseItems()
        {
            // Use the command addon's complete native list close, not just
            // its component Hide.1568EC cancels ALL root timelines and runs
            //156A84/1569C0: uncheck rows, clear the list cursor/hover state and
            // owner.+15E8, then queue Hide. A visual-only close leaves these
            // native selection bindings alive after a confirm/target/Back.
            // owner.+48 has already been restored to original Items here.
            menus._call(0x1568EC,owner,-1,0,0);
            // Only balance our ROOT Show. Recursive component-close would
            // also set ListAction/row.+22A=0. Native156964 reopens the root
            // timeline, not those children, and0F5974 then refuses selection.
            // Keep native list/row shown-state intact across target cancel.
            menus._call(0x408FF8,parent,0,0,0);
            menus.Timeline(parent,160,0x409ECC);
            menus.Timeline(parent,176,0x409824,-1);
            // Native Hide controls the drawing through its timeline.409824
            // only queues the end frame; it does not apply properties here.
            // Keep the original reference/component enabled so normal GUI
            // updates can apply Hide, and subsequent native Show can reopen
            // Items after target cancellation, menu reentry or next turn.
            // These persistent flags are not the native shown-state+22A.
        }
        // Called only after child Dispose AND restoration of the parent's
        // list/descriptor/cursor/preview.22F868 reopens input, not the GUI
        // whose Hide was queued by our child scope. Give this parent modal
        // an explicit visible lifetime, closed again on its confirm/Back.
        internal IDisposable? ReopenItems()
        {
            if(!disposed||!CloseItemsOnDispose||returning||!Current(false))return null;
            int focus=Core!=0&&Ptr(menus._image+0x3CD9DC0)==Core?Int(Core+0xADDC):CoreId;
            returning=true;
            var result=new ParentScope(this,focus);
            try
            {
                Byte(reference+0x90,1);menus.ShowItems(parent);
                if(Core!=0&&Ptr(menus._image+0x3CD9DC0)==Core)Number(Core+0xADDC,Int(parent+8));
                return result;
            }
            catch{result.Dispose();throw;}
        }
        sealed class ParentScope(Scope scope,int focus):IDisposable
        {
            bool closed;
            public void Dispose()
            {
                if(closed)return;closed=true;scope.CloseReturn(focus);
            }
        }
        void CloseReturn(int focus)
        {
            returning=false;
            if(!Current(false))return;
            try{CloseItems();}
            finally
            {
                // Preserve a focus change made by the native modal on exit.
                if(Core!=0&&Ptr(menus._image+0x3CD9DC0)==Core&&Int(Core+0xADDC)==Int(parent+8))
                    Number(Core+0xADDC,focus);
            }
        }
        public void Dispose()
        {
            if(disposed)return;disposed=true;
            if(!Current(true))return;
            Put(owner+0x48,parent);Put(owner+0x1590,titleBinding);
            Byte(child+0x90,0);Byte(component+0x1A0,0);
            // Items can still be visible while its original Hide is in flight
            // when Begin captures it. ShowItems cancels that Hide; restoring
            // the captured visible=1 leaks the passive parent into targeting
            // and subsequent turns. Once a child is successfully opened,
            // always finish closing its passive parent on modal exit. Keep
            // its native update gates intact; do not permanently disable it.
            // Back gets a separate scope around the restored parent modal.
            // A failed Begin still rolls back its original visibility.
            if(CloseItemsOnDispose||parentVisible==0||parentShown==0)
            {
                // Complete native list cleanup plus root-only counterpart of
                // ShowItems'408EBC. Reset root+22A, preserving nested gates.
                // Seeking Hide alone leaves root+22A=1, suppressing next Show.
                CloseItems();
            }
            if(!CloseItemsOnDispose)
            {
                Byte(reference+0x90,referenceVisible);Byte(parent+0x1A0,parentVisible);
            }
            menus._call(0x3EF684,reference,X,Y,0);menus._call(0x3EF684,menu,MenuX,MenuY,0);
            foreach(var (node,cx,cy) in Characters)menus._call(0x3EF684,node,cx,cy,0);
            if(Core!=0&&Ptr(menus._image+0x3CD9DC0)==Core)Number(Core+0xADDC,CoreId);
        }
    }
}

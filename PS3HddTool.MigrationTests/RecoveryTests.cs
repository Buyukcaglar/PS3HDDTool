using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PS3HddTool.Core.Disk;
using PS3HddTool.Core.Migration;

internal static class RecoveryTests
{
    internal static Fixture FixtureWithRecovery(bool collision = false,int groups=4,bool corpus=false)
    {
        var f = new Fixture(groups);
        var detached = f.Add("detached", 4);
        for (int i = 0; i < 3; i++)
        {
            string name = "#"+(f.Items.Count+2).ToString("D9");
            var n = f.Add("detached/"+name, payload: Fixture.Payload(new[] {1,262160,99328}[i], 551+i));
            if (i == 1) n.Ea = Fixture.Payload(17000, 555);
        }
        var sparse = f.Add("detached/#"+(f.Items.Count+2).ToString("D9"), size: 14L*f.Block+13);
        sparse.Data[0] = [11]; sparse.Data[14] = [12];
        f.Add("home", 4); f.Add("home/00000001", 4); f.Add("home/00000001/savedata", 4);
        f.Add("home/00000001/savedata/SAVE.DAT", payload: Fixture.Payload(4101, 581));
        f.Add("home/00000001/trophy", 4); f.Add("home/00000001/trophy/TROPUSR.DAT", payload: Fixture.Payload(8103, 582));
        f.Add("home/00000001/exdata", 4); f.Add("home/00000001/exdata/act.dat", payload: Fixture.Payload(4152, 583));
        f.Add("game", 4);
        f.RawNames.Add("raw-a", [0x90]); f.RawNames.Add("raw-b", [0x91]);
        f.RawNames.Add("raw-c", [0xEF,0xBF,0xBD]); // Valid replacement glyph must remain distinct too.
        f.RawNames.Add("raw-d", Encoding.ASCII.GetBytes("%90"));
        foreach (string name in f.RawNames.Keys) f.Add("game/"+name, payload: Fixture.Payload(77, 600+f.Items.Count));
        f.Add("game/日本語", payload: [1,2,3]);
        if (collision) f.Add(MigrationSource.Container, 4);
        if(corpus)
        {
            foreach(string name in new[]{"J","M","N","P"})f.Add(name,4);
            var retained=f.Add("J/retained.dat",payload:Fixture.Payload(17111,902));
            retained.Ea=Fixture.Payload(17777,903);f.Link("N","hard-link",retained);
            f.Add("M/8GB",payload:Fixture.Payload(33001,904));
            var huge=f.Add("P/sparse-native-like",size:216962957376);
            huge.Data[0]=[37];huge.Data[14]=[38];huge.Data[(huge.Size-1)/f.Block]=[39];
        }
        f.Root.Children.RemoveAll(c => c.Target == detached);
        f.Finish();
        int rootAt = Fixture.Prefix+f.Iblk*f.Frag+2*256;
        BinaryPrimitives.WriteInt16BigEndian(f.Image.AsSpan(rootAt+2), (short)(f.Root.Raw[3]-1));
        int missingAt = Fixture.Prefix+f.Iblk*f.Frag+(int)detached.Ino*256;
        Array.Clear(f.Image, missingAt, 256);
        long pointer = Fixture.R64(detached.Raw, 0x70);
        for (int at = (int)detached.Size; at < f.Frag; at += 512)
            BinaryPrimitives.WriteUInt16BigEndian(f.Image.AsSpan(Fixture.Prefix+(int)pointer*f.Frag+at+4), 512);
        return f;
    }
    private static Fixture Old()
    { var f = new Fixture(8); f.Add("old", payload: Fixture.Payload(13001, 881)); f.Finish(); return f; }
    private static DirectRequest Direct(MigrationRequest r) => new(
        new(r.Source.Path, 32, r.Source.Crypto, r.Source.KeyFile),
        new(r.DestinationTemplate.Path, 32, r.DestinationTemplate.Crypto, r.DestinationTemplate.KeyFile),
        Path.Combine(Path.GetDirectoryName(r.OutputDirectory)!, "journal"), r.SourceRegistry, r.TargetRegistry,
        SourceRecovery: "detached-directory");
    private static void Expect<T>(Action a) where T: Exception
    { try { a(); } catch (T) { return; } throw new Exception("Expected "+typeof(T).Name); }
    internal static void CheckOriginals(Fixture fixture, IDiskSource result, List<InodeMapping> mapping, long offset = Fixture.Prefix)
    {
        var originals = new Fixture(metadataOnly:true); originals.Items.Clear();
        originals.Items.AddRange(fixture.Items.Where(n => n.Ino != 2 && n.Path != "detached"));
        var manifest = new MigrationManifest(1, new(new("unused",32),new("unused",32),"unused"), mapping.Count, mapping, "", null, false);
        Oracle.Check(originals, result.ReadBytes, offset, manifest, auditMaps:false);
    }
    private static byte[] Contents(UfsVolume v, Node n) => Enumerable.Range(0, (int)((n.Size+v.Block-1)/v.Block)).SelectMany(i => v.LogicalBytes(n,i)).ToArray();
    private static void CheckEvidence(Fixture f, IDiskSource result, long sector = 32)
    {
        var v = new UfsVolume(result, sector); v.Inventory(false);
        var container = v.Nodes[v.Nodes[2].Entries.Single(e => e.Text == MigrationSource.Container).Inode];
        var names = container.Entries.Where(e => e.Text is not ("." or "..")).ToDictionary(e => e.Text);
        if (names.Count != 8 || names.Keys.Count(k => k.StartsWith('#')) != 4) throw new Exception("Missing recovery objects.");
        byte[] expected = f.Image.AsSpan(Fixture.Prefix+(int)Fixture.R64(f.Items[1].Raw,0x70)*f.Frag,f.Frag).ToArray();
        Oracle.Equal(expected, Contents(v,v.Nodes[names["original-directory.bin"].Inode]), "exact original recovery fragment");
        Oracle.Equal(new byte[256], Contents(v,v.Nodes[names["original-inode.bin"].Inode]), "original missing inode");
        using var evidence = JsonDocument.Parse(Contents(v,v.Nodes[names["recovery.json"].Inode]));
        if (evidence.RootElement.GetProperty("sourceModified").GetBoolean() || evidence.RootElement.GetProperty("originalDirectoryAttributesKnown").GetBoolean())
            throw new Exception("Invented original attributes.");
        var game = v.Nodes[v.Nodes[2].Entries.Single(e => e.Text == "game").Inode];
        foreach (byte[] name in f.RawNames.Values)
            if (!game.Entries.Any(e => e.Name.AsSpan().SequenceEqual(name))) throw new Exception("Raw filename bytes changed.");
        if (game.Entries.Select(e => e.Key).Distinct().Count() != game.Entries.Count) throw new Exception("Raw-name collision.");
    }
    internal static void RunLarge(string dir)
    {
        var f=FixtureWithRecovery(); var old=Old();
        string source=Path.Combine(dir,"synthetic-source-1TB.img"),target=Path.Combine(dir,"synthetic-target-2TB.img");
        string registry=Path.Combine(dir,"generated-registry.bin");File.WriteAllBytes(registry,Fixture.Registry(("00000001","0123456789ABCDEF")));
        var request=new DirectRequest(new(source,SparseFixture.Offset/512),new(target,SparseFixture.Offset/512),Path.Combine(dir,"journal"),registry,registry,SourceRecovery:"detached-directory");
        var author=new SparseFixture(1000204886016);var targetAuthor=new SparseFixture(2000398934016);
        Console.WriteLine("Authoring synthetic full-capacity sparse files; no physical endpoints");
        author.Create(request.Source,f);targetAuthor.Create(request.Destination,old);
        // The independent streaming author writes valid directory metadata first.
        // Detach its recovery directory by zeroing only its inode, keeping all
        // allocated blocks and recorded directory counters as in the native case.
        using(var raw=new DirectDisk(request.Source,true))
        {
            long inode=SparseFixture.Offset+(long)SparseFixture.Iblk*SparseFixture.Frag;
            byte[] table=raw.ReadBytes(inode,SparseFixture.Block);
            byte[] missing=table.AsSpan(3*256,256).ToArray();long fragment=Fixture.R64(missing,0x70);
            Array.Clear(table,3*256,256);
            short links=BinaryPrimitives.ReadInt16BigEndian(table.AsSpan(2*256+2));BinaryPrimitives.WriteInt16BigEndian(table.AsSpan(2*256+2),(short)(links-1));
            raw.WriteBytes(inode,table);
            byte[] directory=raw.ReadBytes(SparseFixture.Offset+fragment*SparseFixture.Frag,SparseFixture.Frag);
            for(int at=(int)Fixture.R64(missing,0x10);at<SparseFixture.Frag;at+=512)BinaryPrimitives.WriteUInt16BigEndian(directory.AsSpan(at+4),512);
            raw.WriteBytes(SparseFixture.Offset+fragment*SparseFixture.Frag,directory);raw.Flush();
        }
        string original=SparseFile.AllocatedDigest(source),oldTarget=SparseFile.AllocatedDigest(target);
        long peak=0;
        void Measure(){long use=SparseFile.Allocated(source)+SparseFile.Allocated(target)+Directory.GetFiles(dir,"*",SearchOption.AllDirectories).Where(p=>!p.EndsWith(".img")).Sum(p=>new FileInfo(p).Length);
            peak=Math.Max(peak,use);if(use>1024L*1024*1024)throw new Exception("Sparse recovery test exceeded 1GiB host storage budget");}
        Console.WriteLine("Planning synthetic 1TB source recovery and 2TB destination");var plan=DirectMigrator.Plan(request);Measure();
        if(plan.SourceRecovery?.OrphanFiles!=4||SparseFile.AllocatedDigest(target)!=oldTarget||Directory.Exists(request.JournalDirectory))throw new Exception("Plan changed target/lost recovery evidence");
        void Progress(string stage){if(stage.StartsWith("Copy inode")||stage=="Journal snapshot chunk")return;Console.WriteLine(stage);Measure();}
        Console.WriteLine("Copying recovery view through normal direct journal");var state=DirectMigrator.Run(request,Progress);Measure();
        Console.WriteLine("Verifying reopened synthetic destination");DirectMigrator.Verify(request.JournalDirectory);
        using(var disk=RegularImage.Open(request.Destination.Image))
        {
            CheckOriginals(f,disk,state.Mapping,SparseFixture.Offset);
            var v=new UfsVolume(disk,request.Destination.PartitionSector);v.Inventory(false);
            var container=v.Nodes[v.Nodes[2].Entries.Single(e=>e.Text==MigrationSource.Container).Inode];
            if(container.Entries.Count(e=>e.Text.StartsWith('#'))!=4)throw new Exception("Missing recovered file");
            var evidence=container.Entries.Single(e=>e.Text=="original-inode.bin");Oracle.Equal(new byte[256],Contents(v,v.Nodes[evidence.Inode]),"full-size recovery evidence");
        }
        if(SparseFile.AllocatedDigest(source)!=original)throw new Exception("Sparse source changed");Measure();
        File.WriteAllText(Path.Combine(dir,"large-recovery-validation.json"),JsonSerializer.Serialize(new{sourceLogicalBytes=author.DiskBytes,targetLogicalBytes=targetAuthor.DiskBytes,
            sourceUnchanged=true,sourceAllocatedBytes=SparseFile.Allocated(source),targetAllocatedBytes=SparseFile.Allocated(target),peakHostBytes=peak,
            plan.SourceRecovery,state.Phase,syntheticOnly=true,physicalAccess=false},ImageMigrator.Json));
        File.Delete(source);File.Delete(target); // Only these invocation-owned generated images, after verification.
    }
    internal static void Run(Action<string,Action<string>> test,
        Func<string,Fixture,Fixture,string,byte[]?,byte[]?,MigrationRequest> setup, byte[] registry)
    {
        foreach (string crypto in new[] {"plain","cbc192","cbc192-bswap16","xts128"})
            test("recovery-direct-and-package-"+crypto, dir => {
                var f = FixtureWithRecovery(); var r = setup(dir,f,Old(),crypto,registry,registry) with {SourceRecovery="detached-directory"};
                string sourceHash = RegularImage.HashFile(r.Source.Path), targetHash = RegularImage.HashFile(r.DestinationTemplate.Path);
                var package = ImageMigrator.Prepare(r); ImageMigrator.Verify(r.OutputDirectory);
                if (package.SourceRecovery?.OrphanFiles != 4 || !package.SourceRecovery.GeneratedContainerAttributes) throw new Exception("Missing package evidence report.");
                using (var image = RegularImage.Open(r.DestinationTemplate with {Path=Path.Combine(r.OutputDirectory,"disk.img")}))
                { CheckOriginals(f,image,package.Mapping); CheckEvidence(f,image); }
                if (sourceHash != RegularImage.HashFile(r.Source.Path) || targetHash != RegularImage.HashFile(r.DestinationTemplate.Path)) throw new Exception("Prepare changed input.");
                var direct = Direct(r); var plan = DirectMigrator.Plan(direct);
                if (plan.SourceRecovery?.OrphanFiles != 4 || targetHash != RegularImage.HashFile(direct.Destination.Path)) throw new Exception("Plan changed target/lost report.");
                var state = DirectMigrator.Run(direct); DirectMigrator.Verify(direct.JournalDirectory);
                using (var image = RegularImage.Open(direct.Destination.Image)) { CheckOriginals(f,image,state.Mapping); CheckEvidence(f,image); }
                if (sourceHash != RegularImage.HashFile(r.Source.Path)) throw new Exception("Recovery wrote source.");
            });
        foreach (string problem in new[] {"strict-default","dirty","unclean-flag","pending","wrong-parent","wrong-dot","wrong-name","nonzero-missing","two-missing",
            "extra-allocation","overlap","free-reference","bad-summary","bad-directory-count","bad-fragment-summary","bad-cluster-summary","bad-links","duplicate-name","container-collision"})
            test("recovery-refuses-"+problem,dir => {
                var f = FixtureWithRecovery(problem=="container-collision");
                int sb=Fixture.Prefix+65536, cg=Fixture.Prefix+f.Cblk*f.Frag, im=cg+Fixture.R32(f.Image,cg+0x5C), fm=cg+Fixture.R32(f.Image,cg+0x60);
                int inode=Fixture.Prefix+f.Iblk*f.Frag, fragment=Fixture.Prefix+(int)Fixture.R64(f.Items[1].Raw,0x70)*f.Frag;
                int parent=fragment+BinaryPrimitives.ReadUInt16BigEndian(f.Image.AsSpan(fragment+4));
                int firstFile=parent+BinaryPrimitives.ReadUInt16BigEndian(f.Image.AsSpan(parent+4));
                if(problem=="dirty") f.Image[sb+0xD1]=0;
                if(problem=="unclean-flag") Fixture.W32(f.Image,sb+0x520,3);
                if(problem=="pending") Fixture.W64(f.Image,sb+0x450,1);
                if(problem=="wrong-parent") Fixture.W32(f.Image,parent,3);
                if(problem=="wrong-dot") Fixture.W32(f.Image,fragment,4);
                if(problem=="wrong-name") f.Image[firstFile+8]=(byte)'!';
                if(problem=="nonzero-missing") f.Image[inode+3*256+255]=1;
                if(problem=="two-missing") Fixture.Set(f.Image,im,50,true);
                if(problem=="extra-allocation") { int bit=Enumerable.Range(f.Dblk,f.Fpg-f.Dblk).First(i=>Fixture.Get(f.Image,fm,i)); Fixture.Set(f.Image,fm,bit,false); }
                if(problem=="overlap") Fixture.W64(f.Image,inode+4*256+0x70,Fixture.R64(f.Items.Single(n=>n.Path=="game/raw-a").Raw,0x70));
                if(problem=="free-reference") Fixture.Set(f.Image,fm,(int)Fixture.R64(f.Items[2].Raw,0x70),true);
                if(problem=="bad-summary") Fixture.W32(f.Image,Fixture.Prefix+f.Dblk*f.Frag,123);
                if(problem=="bad-directory-count") Fixture.W32(f.Image,cg+0x18,123);
                if(problem=="bad-fragment-summary") f.Image[cg+0x3B]^=1;
                if(problem=="bad-cluster-summary") f.Image[cg+Fixture.R32(f.Image,cg+0x68)+7]^=1;
                if(problem=="bad-links") BinaryPrimitives.WriteInt16BigEndian(f.Image.AsSpan(inode+4*256+2),2);
                if(problem=="duplicate-name") { int next=firstFile+BinaryPrimitives.ReadUInt16BigEndian(f.Image.AsSpan(firstFile+4)); f.Image.AsSpan(firstFile+8,10).CopyTo(f.Image.AsSpan(next+8)); }
                var r=Direct(setup(dir,f,Old(),"plain",registry,registry)); if(problem=="strict-default") r=r with{SourceRecovery=null};
                string source=RegularImage.HashFile(r.Source.Path), target=RegularImage.HashFile(r.Destination.Path);
                Expect<InvalidDataException>(()=>DirectMigrator.Run(r));
                if(source!=RegularImage.HashFile(r.Source.Path)||target!=RegularImage.HashFile(r.Destination.Path)||Directory.Exists(r.JournalDirectory)) throw new Exception("Refusal mutated inputs/journal.");
            });
        foreach (bool crash in new[]{false,true}) test("recovery-"+(crash?"process-loss":"cancellation")+"-rollback",dir=>{
            var f=FixtureWithRecovery(); var old=Old(); var r=Direct(setup(dir,f,old,"cbc192-bswap16",registry,registry));
            string source=RegularImage.HashFile(r.Source.Path); byte[] before=File.ReadAllBytes(r.Destination.Path);
            using var cancel=new CancellationTokenSource();
            void Stop(string s) { if(!s.StartsWith("Copy inode "))return; if(crash)throw new SimulatedProcessLossException("Synthetic process loss"); cancel.Cancel(); }
            if(crash) { Expect<SimulatedProcessLossException>(()=>DirectMigrator.Run(r,Stop));
                File.Move(r.Source.Path,r.Source.Path+".temporarily-away"); DirectMigrator.Recover(r.JournalDirectory); File.Move(r.Source.Path+".temporarily-away",r.Source.Path); }
            else Expect<OperationCanceledException>(()=>DirectMigrator.Run(r,Stop,cancel.Token));
            if(DirectMigrator.Status(r.JournalDirectory).Phase!="rolled-back"||source!=RegularImage.HashFile(r.Source.Path))throw new Exception("Rollback/source preservation failed.");
            using var raw=new DirectDisk(r.Destination,false);
            foreach(var range in DirectLayout.Merge(DirectMigrator.Status(r.JournalDirectory).Metadata.Concat(DirectMigrator.Status(r.JournalDirectory).ProtectedData).Concat(DirectMigrator.Status(r.JournalDirectory).Outside)))
                Oracle.Equal(before.AsSpan((int)range.Offset,(int)range.Length).ToArray(),raw.ReadBytes(range.Offset,(int)range.Length),"rollback original sectors");
        });
        test("recovery-original-evidence-tamper-detected",dir=>{
            var f=FixtureWithRecovery();var r=Direct(setup(dir,f,Old(),"plain",registry,registry));DirectMigrator.Run(r);
            using(var stream=File.Open(r.Source.Path,FileMode.Open,FileAccess.Write)) { stream.Position=Fixture.Prefix+Fixture.R64(f.Items[1].Raw,0x70)*f.Frag+511;stream.WriteByte(0xAA); }
            Expect<InvalidDataException>(()=>DirectMigrator.Verify(r.JournalDirectory));
        });
        test("recovery-target-remains-strict",dir=>{
            var good=Old();var bad=FixtureWithRecovery();var r=Direct(setup(dir,good,bad,"plain",registry,registry)) with{SourceRecovery=null};
            string target=RegularImage.HashFile(r.Destination.Path);Expect<InvalidDataException>(()=>DirectMigrator.Run(r));
            if(target!=RegularImage.HashFile(r.Destination.Path)||Directory.Exists(r.JournalDirectory))throw new Exception("Target orphan guard bypassed.");
        });
        test("recovery-legacy-journal-default-shape",dir=>{
            var r=Direct(setup(dir,Old(),Old(),"plain",null,null)) with{SourceRecovery=null};
            var state=DirectMigrator.Run(r);string json=JsonSerializer.Serialize(state,ImageMigrator.Json);
            if(json.Contains("SourceRecovery"))throw new Exception("Strict default changes legacy canonical journal schema.");
            DirectMigrator.Status(r.JournalDirectory);DirectMigrator.Verify(r.JournalDirectory);
        });
        test("recovery-cli-inline-plan-and-run",dir=>{
            var f=FixtureWithRecovery();var r=Direct(setup(dir,f,Old(),"plain",registry,registry));
            var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
            start.ArgumentList.Add(typeof(PS3HddTool.Migrate.CliOptions).Assembly.Location);
            foreach(string arg in new[]{"direct-run","--source",r.Source.Path,"--destination",r.Destination.Path,"--source-sector","32","--destination-sector","32","--journal",r.JournalDirectory,
                "--source-registry",r.SourceRegistry!,"--target-registry",r.TargetRegistry!,"--source-recovery","detached-directory","--json","--progress-interval-ms","0"})start.ArgumentList.Add(arg);
            using var child=Process.Start(start)!;var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();
            if(!child.WaitForExit(60000)){child.Kill();throw new Exception("Synthetic CLI timeout.");}
            string output=stdout.Result;File.WriteAllText(Path.Combine(dir,"cli.ndjson"),output);
            if(child.ExitCode!=0)throw new Exception(stderr.Result+output);
            var events=output.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(l=>JsonDocument.Parse(l).RootElement.Clone()).ToArray();
            if(!output.Contains("source-recovery-qualified")||!output.Contains("source-recovery-view")||events[^1].GetProperty("type").GetString()!="result")throw new Exception("Missing progress/result.");
            DirectMigrator.Verify(r.JournalDirectory);
        });
    }
}

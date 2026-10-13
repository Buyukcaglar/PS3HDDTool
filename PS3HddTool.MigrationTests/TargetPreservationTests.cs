using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using PS3HddTool.Core.Migration;

internal static class TargetPreservationTests
{
    private static DirectRequest Request(MigrationRequest r,string dir)=>new(
        new(r.Source.Path,32,r.Source.Crypto,r.Source.KeyFile),new(r.DestinationTemplate.Path,32,r.DestinationTemplate.Crypto,r.DestinationTemplate.KeyFile),
        Path.Combine(dir,"journal"),r.SourceRegistry,r.TargetRegistry,TargetPreservation:Path.Combine(dir,"archive"),TargetRecovery:"detached-directory");
    private static void Reject(Action action)
    {try{action();}catch(Exception e)when(e is IOException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException){return;}throw new Exception("Unsafe operation accepted");}
    private static void Unchanged(DirectRequest r,string a,string b)
    {if(RegularImage.HashFile(r.Source.Path)!=a||RegularImage.HashFile(r.Destination.Path)!=b)throw new Exception("Read-only phase changed endpoint");}
    private static void OriginalObjects(Fixture f,string path,string crypto,string? key)
    {
        using var disk=RegularImage.Open(new(path,32,crypto,key));
        var selected=new Fixture(metadataOnly:true);selected.Items.Clear();
        selected.Items.AddRange(f.Items.Where(n=>n.Ino!=2&&n.Path!="detached"&&n.Path!="P/sparse-native-like"));
        var mapping=f.Items.Select(n=>new InodeMapping(n.Ino,n.Ino)).ToList();
        var manifest=new MigrationManifest(1,new(new("unused",32),new("unused",32),"unused"),mapping.Count,mapping,"",null,false);
        Oracle.Check(selected,disk.ReadBytes,Fixture.Prefix,manifest,auditMaps:false);
        foreach(var item in f.Items)
        {
            int at=Fixture.Prefix+f.Iblk*f.Frag+checked((int)item.Ino)*256;
            Oracle.Equal(f.Image.AsSpan(at,256).ToArray(),disk.ReadBytes(at,256),"exact archived inode including detached zero/root");
        }
        var volume=new UfsVolume(disk,32);volume.QualifyDetachedRecovery(default,null);
        var huge=f.Items.Single(n=>n.Path=="P/sparse-native-like");var node=volume.Nodes[huge.Ino];
        if(node.Size!=huge.Size||!volume.NonzeroBlocks(node).Select(x=>x.Logical).SequenceEqual(huge.Data.Keys))throw new Exception("Archived huge sparse topology changed");
        foreach(var pair in huge.Data)Oracle.Equal(pair.Value,volume.LogicalBytes(node,pair.Key).AsSpan(0,pair.Value.Length).ToArray(),"huge sparse allocated bytes");
    }
    private static void RestoredRanges(DirectRequest r,byte[] original,DirectState state)
    {
        using var disk=new DirectDisk(r.Destination with{KeyFile=null},false);
        foreach(var range in state.Metadata.Concat(state.ProtectedData).Concat(state.Outside))
            Oracle.Equal(original.AsSpan((int)range.Offset,(int)range.Length).ToArray(),disk.ReadBytes(range.Offset,(int)range.Length),"rollback original metadata/allocated/outside bytes");
    }
    internal static void Run(Action<string,Action<string>> test,Func<Fixture> profile,
        Func<string,Fixture,Fixture,string,byte[]?,byte[]?,MigrationRequest> setup,byte[] registry)
    {
        foreach(string crypto in new[]{"plain","cbc192","cbc192-bswap16","xts128"})
        test("target-preserve-profile-"+crypto,dir=>{
            var target=RecoveryTests.FixtureWithRecovery(groups:8,corpus:true);
            var r=Request(setup(dir,profile(),target,crypto,registry,registry),dir);
            string a=RegularImage.HashFile(r.Source.Path),b=RegularImage.HashFile(r.Destination.Path);
            var estimated=TargetPreservation.Plan(r);
            if(estimated.OrphanFiles!=4||estimated.ArchiveBytes>=4*1024*1024)throw new Exception("Archive materialized sparse P holes or lost recovery");
            Reject(()=>DirectMigrator.Plan(r));Unchanged(r,a,b);
            TargetPreservationReport saved;
            using(var raw=new DirectDisk(r.Destination,false))saved=TargetPreservation.CreateReadOnly(r,raw,raw.Identity,
                crypto=="plain"?null:File.ReadAllBytes(r.Destination.KeyFile!));
            Unchanged(r,a,b);
            if(saved!=TargetPreservation.Verify(r.TargetPreservation!))throw new Exception("Archive readback report mismatch");
            string export=Path.Combine(dir,"original-target.img");TargetPreservation.Export(r.TargetPreservation!,export);OriginalObjects(target,export,crypto,r.Destination.KeyFile);
            using(var archive=TargetPreservation.Open(r.TargetPreservation!))
            {
                Reject(()=>{using var writer=File.OpenWrite(Path.Combine(r.TargetPreservation!,"preservation.bin"));});
                Reject(()=>{using var writer=File.OpenWrite(Path.Combine(r.TargetPreservation!,"manifest.json"));});
            }
            var plan=DirectMigrator.Plan(r);Unchanged(r,a,b);
            if(plan.TargetPreservation!=saved)throw new Exception("Plan lost archive binding");
            var state=DirectMigrator.Run(r);DirectMigrator.Verify(r.JournalDirectory);
            if(state.Plan.TargetPreservation!=saved||RegularImage.HashFile(r.Source.Path)!=a)throw new Exception("Copy lost archive binding/source changed");
            File.Delete(export);TargetPreservation.Export(r.TargetPreservation!,export);OriginalObjects(target,export,crypto,r.Destination.KeyFile);
            File.WriteAllBytes(Path.Combine(dir,"validation.json"),JsonSerializer.SerializeToUtf8Bytes(new{estimated,saved,sourceUnchanged=true,archiveExportVerified=true,syntheticOnly=true},ImageMigrator.Json));
        });
        foreach(string fault in new[]{"archive-data","archive-address","archive-truncate","manifest","registry","missing","wrong-identity","live-payload","live-recovery-fragment","strict-target","no-archive-mode","duplicate-manifest"})
        test("target-preserve-refuse-"+fault,dir=>{
            var target=RecoveryTests.FixtureWithRecovery(groups:8,corpus:true);var r=Request(setup(dir,profile(),target,"plain",registry,registry),dir);
            TargetPreservation.Create(r);string payload=Path.Combine(r.TargetPreservation!,"preservation.bin"),manifest=Path.Combine(r.TargetPreservation!,"manifest.json");
            if(fault is "archive-data" or "archive-address")
            {using var f=new FileStream(payload,FileMode.Open,FileAccess.ReadWrite);f.Position=fault=="archive-data"?44:0;int n=f.ReadByte();f.Position--;f.WriteByte((byte)(n^1));}
            if(fault=="archive-truncate"){using var f=File.OpenWrite(payload);f.SetLength(f.Length-1);}
            if(fault=="manifest")File.WriteAllText(manifest,File.ReadAllText(manifest).Replace("ArchiveBytes", "ArchiveTypos"));
            if(fault=="duplicate-manifest")File.WriteAllText(manifest,File.ReadAllText(manifest).Replace("\"Version\": 1", "\"Version\": 1, \"Version\": 1"));
            if(fault=="registry"){string file=Path.Combine(r.TargetPreservation!,"target-xRegistry.sys");byte[] bytes=File.ReadAllBytes(file);bytes[100]^=1;File.WriteAllBytes(file,bytes);}
            if(fault=="missing")File.Delete(manifest);
            if(fault=="wrong-identity"){string other=Path.Combine(dir,"other.img");File.Copy(r.Destination.Path,other);r=r with{Destination=r.Destination with{Path=other}};}
            if(fault is "live-payload" or "live-recovery-fragment")
            {
                long ino=fault=="live-payload"?target.Items.Single(n=>n.Path=="M/8GB").Ino:3;
                long p=Fixture.R64(target.Items.Single(n=>n.Ino==ino).Raw,0x70);
                using var f=new FileStream(r.Destination.Path,FileMode.Open,FileAccess.ReadWrite);f.Position=Fixture.Prefix+p*target.Frag+(fault=="live-payload"?0:4000);int n=f.ReadByte();f.Position--;f.WriteByte((byte)(n^1));
            }
            if(fault=="strict-target")r=r with{TargetRecovery=null};
            if(fault=="no-archive-mode")r=r with{TargetPreservation=null};
            string a=RegularImage.HashFile(r.Source.Path),b=RegularImage.HashFile(r.Destination.Path);
            Reject(()=>DirectMigrator.Run(r));Unchanged(r,a,b);
            if(Directory.Exists(r.JournalDirectory))throw new Exception("Rejected preservation created migration journal");
        });
        foreach(string fault in new[]{"dirty","summary","extra-allocation"})
        test("target-preserve-qualification-"+fault,dir=>{
            var target=RecoveryTests.FixtureWithRecovery(groups:8,corpus:true);
            if(fault=="dirty")target.Image[Fixture.Prefix+65536+0xD1]=0;
            if(fault=="summary")target.Image[Fixture.Prefix+65536+0x3F8+7]^=1;
            if(fault=="extra-allocation")
            {int at=Fixture.Prefix+target.Cblk*target.Frag,map=Fixture.R32(target.Image,at+0x60),f=target.Fpg-1;target.Image[at+map+f/8]&=(byte)~(1<<(f%8));}
            var r=Request(setup(dir,profile(),target,"plain",registry,registry),dir);string a=RegularImage.HashFile(r.Source.Path),b=RegularImage.HashFile(r.Destination.Path);
            Reject(()=>TargetPreservation.Create(r));Unchanged(r,a,b);if(Directory.Exists(r.TargetPreservation!))throw new Exception("Bad target published archive directory");
        });
        foreach(bool crash in new[]{false,true})
        test("target-preserve-"+(crash?"source-free-crash-recover":"cancel-rollback"),dir=>{
            var r=Request(setup(dir,profile(),RecoveryTests.FixtureWithRecovery(groups:8,corpus:true),"cbc192-bswap16",registry,registry),dir);
            TargetPreservation.Create(r);byte[] original=File.ReadAllBytes(r.Destination.Path);using var cancel=new CancellationTokenSource();
            try{
                DirectMigrator.Run(r,stage=>{if(stage=="Direct data copy durable"){if(crash)throw new SimulatedProcessLossException("synthetic");cancel.Cancel();}},cancel.Token);
                throw new Exception("Fault did not trigger");
            }catch(Exception e)when(crash?e is SimulatedProcessLossException:e is OperationCanceledException){}
            if(crash){File.Move(r.Source.Path,r.Source.Path+".held");File.Move(r.Source.KeyFile!,r.Source.KeyFile+".held");Directory.Move(r.TargetPreservation!,r.TargetPreservation+".held");DirectMigrator.Recover(r.JournalDirectory);}
            var state=DirectMigrator.Status(r.JournalDirectory);if(state.Phase!="rolled-back")throw new Exception("Rollback incomplete");RestoredRanges(r,original,state);
        });
        test("target-preserve-cancel-archive",dir=>{
            var r=Request(setup(dir,profile(),RecoveryTests.FixtureWithRecovery(groups:8,corpus:true),"plain",registry,registry),dir);
            string a=RegularImage.HashFile(r.Source.Path),b=RegularImage.HashFile(r.Destination.Path);using var c=new CancellationTokenSource();
            try{TargetPreservation.Create(r,c.Token,p=>{if(p.Phase=="target-preserve")c.Cancel();});throw new Exception("Archive cancellation ignored");}
            catch(OperationCanceledException){}Unchanged(r,a,b);if(File.Exists(Path.Combine(r.TargetPreservation!,"manifest.json")))throw new Exception("Cancelled archive published manifest");
        });
        test("target-preserve-completed-session-requires-archive",dir=>{
            var r=Request(setup(dir,profile(),RecoveryTests.FixtureWithRecovery(groups:8,corpus:true),"plain",registry,registry),dir);
            TargetPreservation.Create(r);DirectMigrator.Run(r);Directory.Move(r.TargetPreservation!,r.TargetPreservation+".held");
            Reject(()=>DirectMigrator.Verify(r.JournalDirectory));
        });
        test("target-preserve-readonly-factory-refuses-writer",dir=>{
            var r=Request(setup(dir,profile(),RecoveryTests.FixtureWithRecovery(groups:8,corpus:true),"plain",registry,registry),dir);
            using var raw=new DirectDisk(r.Destination,true);Reject(()=>TargetPreservation.CreateReadOnly(r,raw,raw.Identity,null));
            if(Directory.Exists(r.TargetPreservation!))throw new Exception("Writable transport admitted");
        });
        test("target-preserve-cli-end-to-end",dir=>{
            var r=Request(setup(dir,profile(),RecoveryTests.FixtureWithRecovery(groups:8,corpus:true),"plain",registry,registry),dir);
            string request=Path.Combine(dir,"request.json");File.WriteAllBytes(request,JsonSerializer.SerializeToUtf8Bytes(r,ImageMigrator.Json));
            void Invoke(params string[] arguments)
            {
                var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
                start.ArgumentList.Add(typeof(PS3HddTool.Migrate.CliOptions).Assembly.Location);foreach(string s in arguments)start.ArgumentList.Add(s);start.ArgumentList.Add("--json");
                using var p=Process.Start(start)!;var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();
                if(!p.WaitForExit(60000)){p.Kill();throw new Exception("Synthetic CLI timeout");}
                Task.WaitAll(stdout,stderr);var events=stdout.Result.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(s=>JsonDocument.Parse(s).RootElement.Clone()).ToArray();
                File.WriteAllText(Path.Combine(dir,arguments[0]+".ndjson"),stdout.Result);
                if(p.ExitCode!=0||events[^1].GetProperty("type").GetString()!="result")throw new Exception(stdout.Result+stderr.Result);
            }
            Invoke("target-plan","--request",request);Invoke("target-preserve","--request",request);Invoke("target-verify","--preservation",r.TargetPreservation!);
            Invoke("target-export","--preservation",r.TargetPreservation!,"--output",Path.Combine(dir,"export.img"));Invoke("direct-plan","--request",request);
            Invoke("direct-run","--request",request);Invoke("direct-verify","--journal",r.JournalDirectory);
        });
    }
    internal static void RunLarge(string dir)
    {
        var sourceFixture=RecoveryTests.FixtureWithRecovery();var targetFixture=RecoveryTests.FixtureWithRecovery(groups:8,corpus:true);
        string source=Path.Combine(dir,"source-1TB.img"),target=Path.Combine(dir,"target-2TB.img"),export=Path.Combine(dir,"preserved-2TB.img");
        string registry=Path.Combine(dir,"generated-registry.bin");File.WriteAllBytes(registry,Fixture.Registry(("00000001","0123456789ABCDEF")));
        var request=new DirectRequest(new(source,SparseFixture.Offset/512),new(target,SparseFixture.Offset/512),Path.Combine(dir,"journal"),registry,registry,
            SourceRecovery:"detached-directory",TargetPreservation:Path.Combine(dir,"archive"),TargetRecovery:"detached-directory");
        Console.WriteLine("Author synthetic source1TB/target2TB with detached recovery and huge sparse P file");
        new SparseFixture(1000204886016).Create(request.Source,sourceFixture);new SparseFixture(2000398934016).Create(request.Destination,targetFixture);
        void Detach(DirectEndpoint endpoint)
        {
            using var raw=new DirectDisk(endpoint,true);long at=SparseFixture.Offset+(long)SparseFixture.Iblk*SparseFixture.Frag;
            byte[] table=raw.ReadBytes(at,SparseFixture.Block),missing=table.AsSpan(3*256,256).ToArray();long fragment=Fixture.R64(missing,0x70);
            Array.Clear(table,3*256,256);short links=BinaryPrimitives.ReadInt16BigEndian(table.AsSpan(2*256+2));BinaryPrimitives.WriteInt16BigEndian(table.AsSpan(2*256+2),(short)(links-1));raw.WriteBytes(at,table);
            byte[] directory=raw.ReadBytes(SparseFixture.Offset+fragment*SparseFixture.Frag,SparseFixture.Frag);
            for(int pos=(int)Fixture.R64(missing,0x10);pos<SparseFixture.Frag;pos+=512)BinaryPrimitives.WriteUInt16BigEndian(directory.AsSpan(pos+4),512);
            raw.WriteBytes(SparseFixture.Offset+fragment*SparseFixture.Frag,directory);raw.Flush();
        }
        Detach(request.Source);Detach(request.Destination);
        string sourceBefore=SparseFile.AllocatedDigest(source),targetBefore=SparseFile.AllocatedDigest(target);
        List<DiskRange> originalRanges;string originalDigest;
        using(var raw=new DirectDisk(request.Destination,false))
        {var volume=new UfsVolume(raw,request.Destination.PartitionSector);TargetPreservation.Inventory(volume,request.TargetRecovery,default,null);originalRanges=TargetPreservation.Ranges(volume);originalDigest=DirectLayout.HashRanges(raw,originalRanges);}
        long peak=0;void Measure()
        {
            long bytes=Directory.GetFiles(dir,"*",SearchOption.AllDirectories).Sum(p=>p.EndsWith(".img")?SparseFile.Allocated(p):new FileInfo(p).Length);
            peak=Math.Max(peak,bytes);if(bytes>2L*1024*1024*1024)throw new Exception("Target preservation test exceeds2GiB host budget");
        }
        Console.WriteLine("Archive and verify original target without materializing P holes");var estimate=TargetPreservation.Plan(request);var saved=TargetPreservation.Create(request);Measure();
        if(saved.ArchiveBytes>512L*1024*1024||SparseFile.AllocatedDigest(source)!=sourceBefore||SparseFile.AllocatedDigest(target)!=targetBefore)throw new Exception("Archive bloated or changed input");
        Console.WriteLine("Plan and copy with both source/target recovery qualification");var plan=DirectMigrator.Plan(request);Measure();
        if(plan.SourceRecovery?.OrphanFiles!=4||plan.TargetPreservation!=saved)throw new Exception("Missing combined qualification");
        var state=DirectMigrator.Run(request,stage=>{if(stage.StartsWith("Copy inode")||stage=="Journal snapshot chunk")return;Console.WriteLine(stage);Measure();});Measure();DirectMigrator.Verify(request.JournalDirectory);
        using(var disk=RegularImage.Open(request.Destination.Image))RecoveryTests.CheckOriginals(sourceFixture,disk,state.Mapping,SparseFixture.Offset);
        Console.WriteLine("Export original target to sparse host image and compare all preserved raw ranges");TargetPreservation.Export(request.TargetPreservation!,export);Measure();
        using(var raw=new DirectDisk(request.Destination with{Path=export,KeyFile=null},false))
        {if(DirectLayout.HashRanges(raw,originalRanges)!=originalDigest)throw new Exception("Original target export lost allocated/metadata/outside bytes");}
        using(var raw=RegularImage.Open(request.Destination.Image with{Path=export}))
        {
            using var view=MigrationSource.Open(raw,request.Destination.PartitionSector,"detached-directory",default,null);
            view.Volume.Inventory(false);var volume=view.Volume;
            var p=volume.Nodes[volume.Nodes[2].Entries.Single(e=>e.Text=="P").Inode];var huge=volume.Nodes[p.Entries.Single(e=>e.Text=="sparse-native-like").Inode];
            var expected=targetFixture.Items.Single(n=>n.Path=="P/sparse-native-like");
            if(huge.Size!=expected.Size||!volume.NonzeroBlocks(huge).Select(x=>x.Logical).SequenceEqual(expected.Data.Keys))throw new Exception("Archived huge sparse file changed");
            foreach(var pair in expected.Data)Oracle.Equal(pair.Value,volume.LogicalBytes(huge,pair.Key).AsSpan(0,pair.Value.Length).ToArray(),"original huge sparse target payload");
        }
        if(SparseFile.AllocatedDigest(source)!=sourceBefore)throw new Exception("Source changed");
        File.WriteAllBytes(Path.Combine(dir,"large-target-preservation-validation.json"),JsonSerializer.SerializeToUtf8Bytes(new{sourceBytes=1000204886016,targetBytes=2000398934016,estimate,saved,peakHostBytes=peak,sourceUnchanged=true,originalTargetExportVerified=true,physicalAccess=false,syntheticOnly=true},ImageMigrator.Json));
        foreach(string owned in new[]{source,target,export})File.Delete(owned);
    }
}

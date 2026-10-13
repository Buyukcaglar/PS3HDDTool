using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using PS3HddTool.Core.Migration;

internal static class RootKeyTests
{
    // Independent AES seed oracle; all roots, keys, images and registries here are generated.
    private static byte[] Derived(byte[] root,string crypto)
    {
        using var aes=Aes.Create();aes.Key=root[..32];
        byte[] data=aes.EncryptCbc(Convert.FromHexString("D92D65DB057D49E1A66F2274B8BAC50883844ED756CA79516362EA8ADAC60326"),root.AsSpan(32),PaddingMode.None);
        if(crypto!="xts128")return data[..24];
        byte[] tweak=aes.EncryptCbc(Convert.FromHexString("C3B3B5AACC74CD6A48EFABF44DCDF16E379F55F5777D09FBEEDE07058E94BE08"),root.AsSpan(32),PaddingMode.None);
        return [..data[..16],..tweak[..16]];
    }
    private static void Reject(Action action)
    {try{action();}catch(Exception e)when(e is ArgumentException or NotSupportedException){return;}throw new Exception("Invalid key configuration accepted");}
    internal static void Run(Action<string,Action<string>> test,Func<Fixture> profile,
        Func<string,Fixture,Fixture,string,byte[]?,byte[]?,MigrationRequest> setup,byte[] registry)
    {
        foreach(string crypto in new[]{"cbc192","cbc192-bswap16","xts128"})
        test("root-key-direct-preservation-"+crypto,dir=>{
            var fixture=profile();var old=RecoveryTests.FixtureWithRecovery(groups:8,corpus:true);
            var prepared=setup(dir,fixture,old,"plain",registry,registry);
            byte[] root=Fixture.Payload(48,812);string rootPath=Path.Combine(dir,"generated-root.key"),derivedPath=Path.Combine(dir,"generated-derived.key");
            File.WriteAllBytes(rootPath,root);File.WriteAllBytes(derivedPath,Derived(root,crypto));
            foreach(string image in new[]{prepared.Source.Path,prepared.DestinationTemplate.Path})
            {byte[] plaintext=File.ReadAllBytes(image);using var encrypted=RegularImage.Open(new(image,32,crypto,derivedPath),true);encrypted.WriteBytes(0,plaintext);RegularImage.Flush(encrypted);}
            var source=new DirectEndpoint(prepared.Source.Path,32,crypto,rootPath,KeyKind:"eid-root");
            var target=new DirectEndpoint(prepared.DestinationTemplate.Path,32,crypto,rootPath,KeyKind:"eid-root");
            var request=new DirectRequest(source,target,Path.Combine(dir,"journal"),prepared.SourceRegistry,prepared.TargetRegistry,
                TargetPreservation:Path.Combine(dir,"archive"),TargetRecovery:"detached-directory");
            string before=RegularImage.HashFile(source.Path);
            using(var disk=RegularImage.Open(source.Image))Oracle.Equal(fixture.Image,disk.ReadBytes(0,fixture.Image.Length),"independent root-key decryption");
            string[] inline=["--source",source.Path,"--destination",target.Path,"--source-sector","32","--destination-sector","32","--journal",request.JournalDirectory,
                "--crypto",crypto,"--key-file",rootPath,"--key-kind","eid-root","--source-registry",prepared.SourceRegistry!,"--target-registry",prepared.TargetRegistry!,
                "--target-preservation",request.TargetPreservation!,"--target-recovery","detached-directory"];
            if(crypto=="cbc192-bswap16")Invoke(dir,"target-preserve",inline);else TargetPreservation.Create(request);
            var plan=DirectMigrator.Plan(request);
            // Root and explicitly derived representations must prove the same actual HDD key.
            var mixed=DirectMigrator.Plan(request with{Destination=target with{KeyFile=derivedPath,KeyKind="derived"}});
            if(mixed!=plan)throw new Exception("Equivalent key representations planned differently");
            byte[] originalRaw=File.ReadAllBytes(target.Path);List<DiskRange> originalRanges;
            using(var disk=RegularImage.Open(target.Image)){var volume=new UfsVolume(disk,32);TargetPreservation.Inventory(volume,"detached-directory",default,null);originalRanges=TargetPreservation.Ranges(volume);}
            DirectState state;
            if(crypto=="cbc192-bswap16"){Invoke(dir,"direct-plan",inline);Invoke(dir,"direct-run",inline);Invoke(dir,"direct-verify",["--journal",request.JournalDirectory]);state=DirectMigrator.Status(request.JournalDirectory);}
            else state=DirectMigrator.Run(request);
            DirectMigrator.Verify(request.JournalDirectory);
            if(state.Phase!="complete"||RegularImage.HashFile(source.Path)!=before)throw new Exception("Root-key migration/source boundary");
            using(var disk=RegularImage.Open(target.Image))
            {
                var manifest=new MigrationManifest(1,new(source.Image,target.Image,"unused"),state.Mapping.Count,state.Mapping,"",null,false);
                Oracle.Check(fixture,disk.ReadBytes(0,(int)disk.TotalSize),manifest);
            }
            string exported=Path.Combine(dir,"old-target.img");TargetPreservation.Export(request.TargetPreservation!,exported);
            using(var raw=new PS3HddTool.Core.Disk.ImageDiskSource(exported))foreach(var range in originalRanges)
                Oracle.Equal(originalRaw.AsSpan((int)range.Offset,(int)range.Length).ToArray(),raw.ReadBytes(range.Offset,(int)range.Length),"original target root-key archive export");
            string json=JsonSerializer.Serialize(state,ImageMigrator.Json);
            foreach(byte[] secret in new[]{root,Derived(root,crypto)})
                if(json.Contains(Convert.ToHexString(secret),StringComparison.OrdinalIgnoreCase)||json.Contains(Convert.ToBase64String(secret)))throw new Exception("Key bytes leaked into journal report");
            if(Directory.GetFiles(dir,"*.key",SearchOption.AllDirectories).Length!=2)throw new Exception("Unexpected key export");
        });
        foreach(string fault in new[]{"missing-opt-in","root-short","derived-as-root","root-as-plain","unknown-kind","different-root"})
        test("root-key-refuse-"+fault,dir=>{
            var old=new Fixture(8);old.Finish(true);var prepared=setup(dir,profile(),old,"plain",registry,registry);
            string rootPath=Path.Combine(dir,"generated-root.key");byte[] root=Fixture.Payload(48,824);File.WriteAllBytes(rootPath,root);
            var source=new DirectEndpoint(prepared.Source.Path,32,"cbc192-bswap16",rootPath,KeyKind:"eid-root");
            var target=source with{Path=prepared.DestinationTemplate.Path};
            if(fault=="missing-opt-in")source=source with{KeyKind=null};
            if(fault=="root-short")File.WriteAllBytes(rootPath,root[..47]);
            if(fault=="derived-as-root")File.WriteAllBytes(rootPath,Derived(root,"cbc192-bswap16"));
            if(fault=="root-as-plain")source=source with{Crypto="plain"};
            if(fault=="unknown-kind")source=source with{KeyKind="guess"};
            if(fault=="different-root"){string other=Path.Combine(dir,"other-root.key");File.WriteAllBytes(other,Fixture.Payload(48,825));target=target with{KeyFile=other};}
            string a=RegularImage.HashFile(source.Path),b=RegularImage.HashFile(target.Path);var request=new DirectRequest(source,target,Path.Combine(dir,"journal"),prepared.SourceRegistry,prepared.TargetRegistry);
            Reject(()=>DirectMigrator.Run(request));
            if(Directory.Exists(request.JournalDirectory)||RegularImage.HashFile(source.Path)!=a||RegularImage.HashFile(target.Path)!=b)throw new Exception("Rejected root configuration wrote endpoint/journal");
        });
        test("root-key-cli-options-and-legacy-json",dir=>{
            var source=new DirectEndpoint("source.img",32,"cbc192-bswap16","generated.key");
            if(JsonSerializer.Serialize(source,ImageMigrator.Json).Contains("KeyKind"))throw new Exception("Legacy endpoint JSON changed");
            if(JsonSerializer.Serialize(source.Image,ImageMigrator.Json).Contains("KeyKind"))throw new Exception("Legacy image JSON changed");
            var options=PS3HddTool.Migrate.CliOptions.Parse(["direct-plan","--source","s.img","--destination","t.img","--source-sector","32","--destination-sector","32","--journal","j","--crypto","cbc192-bswap16","--key-file","root.key","--key-kind","eid-root"]);
            var r=options.DirectRequest();if(r.Source.KeyKind!="eid-root"||r.Destination.KeyKind!="eid-root")throw new Exception("CLI lost key kind");
            try{PS3HddTool.Migrate.CliOptions.Parse(["direct-plan","--request","r.json","--key-kind","eid-root"]);throw new Exception("Request override accepted");}catch(PS3HddTool.Migrate.CliUsageException){}
        });
        test("root-key-recovery-without-root",dir=>{
            var old=new Fixture(8);old.Add("old-file",payload:Fixture.Payload(10003,816));old.Finish(true);
            var prepared=setup(dir,profile(),old,"plain",registry,registry);byte[] root=Fixture.Payload(48,826);
            string rootPath=Path.Combine(dir,"generated-root.key"),keyPath=Path.Combine(dir,"generated-derived.key");File.WriteAllBytes(rootPath,root);File.WriteAllBytes(keyPath,Derived(root,"cbc192-bswap16"));
            foreach(string path in new[]{prepared.Source.Path,prepared.DestinationTemplate.Path})
            {byte[] bytes=File.ReadAllBytes(path);using var disk=RegularImage.Open(new(path,32,"cbc192-bswap16",keyPath),true);disk.WriteBytes(0,bytes);RegularImage.Flush(disk);}
            var request=new DirectRequest(new(prepared.Source.Path,32,"cbc192-bswap16",rootPath,KeyKind:"eid-root"),new(prepared.DestinationTemplate.Path,32,"cbc192-bswap16",rootPath,KeyKind:"eid-root"),Path.Combine(dir,"journal"),prepared.SourceRegistry,prepared.TargetRegistry);
            byte[] original=File.ReadAllBytes(request.Destination.Path);
            try{DirectMigrator.Run(request,stage=>{if(stage=="Direct data copy durable")throw new SimulatedProcessLossException("synthetic root loss");});throw new Exception("Crash not triggered");}catch(SimulatedProcessLossException){}
            File.Move(rootPath,rootPath+".held");File.Move(keyPath,keyPath+".held");File.Move(request.Source.Path,request.Source.Path+".held");
            var state=DirectMigrator.Recover(request.JournalDirectory);
            using var raw=new DirectDisk(request.Destination with{KeyFile=null},false);
            foreach(var range in state.Metadata.Concat(state.ProtectedData).Concat(state.Outside))Oracle.Equal(original.AsSpan((int)range.Offset,(int)range.Length).ToArray(),raw.ReadBytes(range.Offset,(int)range.Length),"root-free rollback");
        });
    }
    private static void Invoke(string dir,string command,string[] arguments)
    {
        var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true,WorkingDirectory=dir};
        start.ArgumentList.Add(typeof(PS3HddTool.Migrate.CliOptions).Assembly.Location);start.ArgumentList.Add(command);
        foreach(string argument in arguments)start.ArgumentList.Add(argument);start.ArgumentList.Add("--json");
        using var process=Process.Start(start)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(60000)){process.Kill();throw new Exception("Synthetic root-key CLI timed out");}
        Task.WaitAll(stdout,stderr);File.WriteAllText(Path.Combine(dir,command+".ndjson"),stdout.Result);
        var events=stdout.Result.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(s=>JsonDocument.Parse(s).RootElement.Clone()).ToArray();
        if(process.ExitCode!=0||stderr.Result.Length!=0||events[^1].GetProperty("type").GetString()!="result")throw new Exception(stdout.Result+stderr.Result);
    }
}

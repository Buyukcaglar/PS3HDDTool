using System.Security.Cryptography;
using System.Text.Json;
using PS3HddTool.Core.Disk;
using PS3HddTool.Core.Migration;

static class VerificationResumeTests
{
    internal static void Run(Action<string, Action<string>> test, Func<Fixture> profile,
        Func<string, Fixture, Fixture, string, byte[]?, byte[]?, MigrationRequest> setup, byte[] registry)
    {
        DirectRequest Prepare(string dir, string crypto = "plain")
        {
            var old = new Fixture(8); old.Add("old", payload: Fixture.Payload(17003, 81)); old.Finish(true);
            var r = setup(dir, profile(), old, crypto, registry, registry);
            return new(new(r.Source.Path,32,crypto,r.Source.KeyFile),new(r.DestinationTemplate.Path,32,crypto,r.DestinationTemplate.KeyFile),
                Path.Combine(dir,"journal"),r.SourceRegistry,r.TargetRegistry);
        }
        void Pause(DirectRequest r)
        {
            using var cancel = new CancellationTokenSource();
            try { DirectMigrator.Run(r, s => { if (s == "Verify direct destination") cancel.Cancel(); }, cancel.Token); }
            catch (VerificationPausedException) { }
            if (DirectMigrator.Status(r.JournalDirectory).Phase != "verification-paused") throw new Exception("Verification did not pause.");
            using var disk = RegularImage.Open(r.Destination.Image);
            if (new UfsVolume(disk,32,true).Super[0xD1] != 0) throw new Exception("Paused target was made clean.");
        }
        List<DiskRange> SourcePayload(DirectRequest r)
        {
            using var disk=RegularImage.Open(r.Source.Image);var v=new UfsVolume(disk,32);v.Inventory(false);
            return v.Nodes.Values.Where(n=>n.Type is 8 or 10 && !n.Inline).SelectMany(n=>v.NonzeroBlocks(n)
                .Select(b=>new DiskRange(v.Offset+b.Fragment*v.Fragment,v.DataLength(n,b.Logical)))).ToList();
        }
        foreach(string crypto in new[]{"plain","cbc192-bswap16","xts128"})
            test("verification-resume-no-source-payload-"+crypto,dir=>{
                var r=Prepare(dir,crypto);Pause(r);var denied=SourcePayload(r);long writes=0;
                var result=DirectMigrator.ResumeVerificationCore(r.JournalDirectory,default,(e,w)=>
                    new ObservedDisk(new DirectDisk(e,w),w?[]:denied,w?()=>writes++:null));
                if(result.Phase!="complete"||writes==0)throw new Exception("Resume did not commit.");
                DirectMigrator.Verify(r.JournalDirectory);
            });
        test("verification-resume-pause-again-no-writes",dir=>{
            var r=Prepare(dir);Pause(r);string before=RegularImage.HashFile(r.Destination.Path);
            using var cancel=new CancellationTokenSource();
            try{DirectMigrator.ResumeVerification(r.JournalDirectory,cancel.Token,p=>{if(p.Phase=="verify-destination-hash")cancel.Cancel();});}
            catch(VerificationPausedException){}
            if(DirectMigrator.Status(r.JournalDirectory).Phase!="verification-paused"||RegularImage.HashFile(r.Destination.Path)!=before)
                throw new Exception("Re-pause wrote to destination.");
            DirectMigrator.ResumeVerification(r.JournalDirectory);
        });
        foreach(string corrupt in new[]{"source-checkpoint","source-metadata","undo","registry","identity","clean-marker","protected-data","phase"})
            test("verification-resume-reject-"+corrupt,dir=>{
                var r=Prepare(dir);Pause(r);var state=DirectMigrator.Status(r.JournalDirectory);
                switch(corrupt){
                    case "source-checkpoint": File.AppendAllText(Path.Combine(r.JournalDirectory,"verification-source-hashes.json")," ");break;
                    case "source-metadata": using(var disk=RegularImage.Open(r.Source.Image,true)){var v=new UfsVolume(disk,32);var n=v.ReadNode(2);n.Raw[7]^=1;RegularImage.Write(disk,v.InodeOffset(2),n.Raw);RegularImage.Flush(disk);}break;
                    case "undo": using(var f=File.OpenWrite(Path.Combine(r.JournalDirectory,"undo.bin"))){f.WriteByte(77);}break;
                    case "registry": File.AppendAllText(r.SourceRegistry!," ");break;
                    case "identity": DirectJournal.Save(r.JournalDirectory,state with{Plan=state.Plan with{DestinationIdentity="wrong"}});break;
                    case "phase": DirectJournal.Save(r.JournalDirectory,state with{Phase="copying"});break;
                    case "protected-data": using(var f=File.Open(r.Destination.Path,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){f.Position=state.ProtectedData[0].Offset;int b=f.ReadByte();f.Position--;f.WriteByte((byte)(b^128));}break;
                    case "clean-marker": using(var f=File.OpenWrite(r.Destination.Path)){f.Position=Fixture.Prefix+65536+0xD1;f.WriteByte(1);}break;
                }
                string before=RegularImage.HashFile(r.Destination.Path);bool failed=false;
                try{DirectMigrator.ResumeVerification(r.JournalDirectory);}catch(Exception e)when(e is InvalidDataException or InvalidOperationException){failed=true;}
                if(!failed||RegularImage.HashFile(r.Destination.Path)!=before)throw new Exception("Rejected checkpoint wrote to destination.");
            });
        test("verification-resume-payload-corruption-rolls-back",dir=>{
            var r=Prepare(dir);string oldDigest;
            using(var d=RegularImage.Open(r.Destination.Image)){var v=new UfsVolume(d,32);v.Inventory(true);oldDigest=DirectLayout.TreeDigest(v);}
            Pause(r);
            using(var disk=RegularImage.Open(r.Destination.Image,true)){
                var v=new UfsVolume(disk,32,true);v.Inventory(false);var n=v.Nodes.Values.First(n=>n.Type==8&&n.Size>0);
                long at=v.Offset+v.NonzeroBlocks(n).First().Fragment*v.Fragment;byte[] b=disk.ReadBytes(at,512);b[0]^=128;disk.WriteBytes(at,b);RegularImage.Flush(disk);
            }
            bool failed=false;try{DirectMigrator.ResumeVerification(r.JournalDirectory);}catch(InvalidDataException){failed=true;}
            using var result=RegularImage.Open(r.Destination.Image);var check=new UfsVolume(result,32);check.Inventory(true);
            if(!failed||DirectMigrator.Status(r.JournalDirectory).Phase!="rolled-back"||DirectLayout.TreeDigest(check)!=oldDigest)
                throw new Exception("Failed resumed verification did not restore original tree.");
        });
        test("verification-resume-legacy-verifying-pinned-cache",dir=>{
            var r=Prepare(dir);Pause(r);var state=DirectMigrator.Status(r.JournalDirectory);
            string cache=Path.Combine(r.JournalDirectory,"verification-source-hashes.json");
            DirectJournal.Save(r.JournalDirectory,state with{Phase="verifying",VerificationSourceHashSha256=null,
                Request=state.Request with{SourceHashCache=cache,SourceHashCacheSha256=RegularImage.HashFile(cache)}});
            var result=DirectMigrator.ResumeVerification(r.JournalDirectory);
            if(result.Phase!="complete")throw new Exception("Legacy verification checkpoint failed.");
        });
        test("verification-resume-durable-process-loss",dir=>{
            var r=Prepare(dir);
            try{DirectMigrator.Run(r,s=>{if(s=="Verify direct destination")throw new SimulatedProcessLossException("Synthetic process loss after durable copy");});}
            catch(SimulatedProcessLossException){}
            if(DirectMigrator.Status(r.JournalDirectory).Phase!="verifying")throw new Exception("No durable verification checkpoint.");
            DirectMigrator.ResumeVerification(r.JournalDirectory);DirectMigrator.Verify(r.JournalDirectory);
        });
        test("verification-resume-commit-failure-rolls-back",dir=>{
            var r=Prepare(dir,"cbc192-bswap16");string oldDigest;
            using(var d=RegularImage.Open(r.Destination.Image)){var v=new UfsVolume(d,32);v.Inventory(true);oldDigest=DirectLayout.TreeDigest(v);}
            Pause(r);bool failed=false;
            try{DirectMigrator.ResumeVerificationCore(r.JournalDirectory,default,(e,w)=>w?new CommitFailureDisk(new DirectDisk(e,w)):new DirectDisk(e,w));}
            catch(IOException e)when(e.Message=="Synthetic resume commit failure"){failed=true;}
            using var result=RegularImage.Open(r.Destination.Image);var check=new UfsVolume(result,32);check.Inventory(true);
            if(!failed||DirectMigrator.Status(r.JournalDirectory).Phase!="rolled-back"||DirectLayout.TreeDigest(check)!=oldDigest)
                throw new Exception("Resume commit failure did not restore original tree.");
        });
        test("verification-resume-exclusive-target-guard",dir=>{
            var r=Prepare(dir);Pause(r);string before=RegularImage.HashFile(r.Destination.Path);
            using(var held=new DirectDisk(r.Destination,true)){
                bool failed=false;try{DirectMigrator.ResumeVerification(r.JournalDirectory);}catch(IOException){failed=true;}
                if(!failed)throw new Exception("Resume acquired an already owned target.");
            }
            if(RegularImage.HashFile(r.Destination.Path)!=before)throw new Exception("Exclusive rejection wrote target.");
        });
        test("verification-resume-read-batching-oracle",dir=>{
            var fixture=new Fixture(112);fixture.Add("large",payload:Fixture.Payload(64*1024*1024+731,51));fixture.Finish();
            string path=Path.Combine(dir,"generated.img");File.WriteAllBytes(path,fixture.Image);
            using var raw=new ObservedDisk(new DirectDisk(new(path,32),false),[],null);
            var volume=new UfsVolume(raw,32);volume.Inventory(false);var node=volume.Nodes.Values.Single(n=>n.Type==8);
            raw.ReadCalls=0;raw.ReadBytesTotal=0;raw.MaxRead=0;string actual=volume.Hash(node);long optimizedCalls=raw.ReadCalls,optimizedBytes=raw.ReadBytesTotal;int maxRead=raw.MaxRead;
            raw.ReadCalls=0;raw.ReadBytesTotal=0;
            using var legacy=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for(long logical=0;logical*volume.Block<node.Size;logical++)legacy.AppendData(volume.LogicalBytes(node,logical));
            string expected=Convert.ToHexString(legacy.GetHashAndReset());
            if(actual!=expected||optimizedCalls*10>=raw.ReadCalls||maxRead<=16384||maxRead>1024*1024)throw new Exception("Batched hashing changed digest or failed to reduce I/O.");
            File.WriteAllText(Path.Combine(dir,"read-comparison.json"),JsonSerializer.Serialize(new{optimizedCalls,optimizedBytes,maxRead,legacyCalls=raw.ReadCalls,legacyBytes=raw.ReadBytesTotal},ImageMigrator.Json));
        });
    }
    sealed class CommitFailureDisk(IIdentifiedDisk raw):IIdentifiedDisk
    {
        bool fired;
        public string Identity=>raw.Identity;public long TotalSize=>raw.TotalSize;public int SectorSize=>512;public long SectorCount=>raw.SectorCount;
        public string Description=>raw.Description;public bool CanWrite=>true;
        public byte[] ReadBytes(long at,int count)=>raw.ReadBytes(at,count);public byte[] ReadSectors(long at,int count)=>raw.ReadSectors(at,count);
        public void WriteBytes(long at,byte[] bytes)=>WriteBytes(at,bytes.AsSpan());
        public void WriteBytes(long at,ReadOnlySpan<byte> bytes){raw.WriteBytes(at,bytes);if(!fired){fired=true;raw.Flush();throw new IOException("Synthetic resume commit failure");}}
        public void WriteSectors(long at,byte[] bytes)=>WriteBytes(checked(at*512),bytes);public void Flush()=>raw.Flush();public void Dispose()=>raw.Dispose();
    }
    sealed class ObservedDisk(IIdentifiedDisk raw,List<DiskRange> denied,Action? write):IIdentifiedDisk
    {
        public long ReadCalls,ReadBytesTotal;public int MaxRead;
        public string Identity=>raw.Identity;public long TotalSize=>raw.TotalSize;public int SectorSize=>512;public long SectorCount=>raw.SectorCount;
        public string Description=>raw.Description;public bool CanWrite=>raw.CanWrite;
        public byte[] ReadBytes(long at,int count){if(denied.Any(r=>at<r.Offset+r.Length&&r.Offset<at+count))throw new Exception("Resume read source payload.");ReadCalls++;ReadBytesTotal+=count;MaxRead=Math.Max(MaxRead,count);return raw.ReadBytes(at,count);}
        public byte[] ReadSectors(long at,int count)=>ReadBytes(checked(at*512),checked(count*512));
        public void WriteBytes(long at,byte[] bytes)=>WriteBytes(at,bytes.AsSpan());
        public void WriteBytes(long at,ReadOnlySpan<byte> bytes){if(bytes.Length!=512)throw new Exception("Resume issued non-marker write.");write?.Invoke();raw.WriteBytes(at,bytes);}
        public void WriteSectors(long at,byte[] bytes)=>WriteBytes(checked(at*512),bytes);public void Flush()=>raw.Flush();public void Dispose()=>raw.Dispose();
    }
}

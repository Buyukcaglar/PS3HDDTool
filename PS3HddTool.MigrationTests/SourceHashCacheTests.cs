using System.Security.Cryptography;
using System.Text.Json;
using PS3HddTool.Core.Migration;

static class SourceHashCacheTests
{
    internal static void Run(Action<string,Action<string>> test,Func<Fixture> profile,
        Func<string,Fixture,Fixture,string,byte[]?,byte[]?,MigrationRequest> setup,byte[] registry)
    {
        DirectRequest Prepare(string dir,string crypto="plain"){
            var old=new Fixture(8);old.Add("old",payload:Fixture.Payload(8192,53));old.Finish(true);
            var r=setup(dir,profile(),old,crypto,registry,registry);
            using var disk=RegularImage.Open(r.Source);var volume=new UfsVolume(disk,32);volume.Inventory(true);
            var snapshot=new SourceHashSnapshot(1,DirectLayout.TreeDigest(volume),volume.Nodes.Values.Select(n=>new SourceHashNode(n.Number,n.Raw,n.Hash,n.Entries)).ToList());
            string path=Path.Combine(dir,"source-hashes.json");File.WriteAllBytes(path,JsonSerializer.SerializeToUtf8Bytes(snapshot,ImageMigrator.Json));
            return new(new(r.Source.Path,32,crypto,r.Source.KeyFile),new(r.DestinationTemplate.Path,32,crypto,r.DestinationTemplate.KeyFile),Path.Combine(dir,"journal"),r.SourceRegistry,r.TargetRegistry,
                SourceHashCache:path,SourceHashCacheSha256:RegularImage.HashFile(path));
        }
        void ExpectFailure(Action act){try{act();}catch(InvalidDataException){return;}throw new Exception("Expected source hash rejection");}
        foreach(string crypto in new[]{"plain","cbc192-bswap16","xts128"})test("source-cache-success-"+crypto,dir=>{
            var r=Prepare(dir,crypto);bool reused=false,prehashed=false;
            var state=DirectMigrator.Run(r,s=>{if(s.StartsWith("Reuse validated"))reused=true;},telemetry:p=>{if(p.Phase=="source-hash")prehashed=true;});
            if(!reused||prehashed||state.Phase!="complete")throw new Exception("Cache did not skip only the pre-hash pass");
            DirectMigrator.Verify(r.JournalDirectory);
        });
        test("source-cache-checksum-reject-before-write",dir=>{
            var r=Prepare(dir);string old=RegularImage.HashFile(r.Destination.Path);
            File.AppendAllText(r.SourceHashCache!," ");ExpectFailure(()=>DirectMigrator.Run(r));
            if(old!=RegularImage.HashFile(r.Destination.Path)||Directory.Exists(r.JournalDirectory))throw new Exception("Rejected cache caused writes");
        });
        test("source-cache-content-change-during-copy-rollback",dir=>{
            var r=Prepare(dir);long at;
            using(var disk=RegularImage.Open(r.Source.Image)){var v=new UfsVolume(disk,32);v.Inventory(false);var n=v.Nodes.Values.First(n=>n.Type==8&&n.Size>0);at=v.Offset+v.NonzeroBlocks(n).First().Fragment*v.Fragment;}
            using(var file=File.Open(r.Source.Path,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){file.Position=at;int value=file.ReadByte();file.Position=at;file.WriteByte((byte)(value^128));file.Flush(true);}
            ExpectFailure(()=>DirectMigrator.Run(r));
            if(DirectMigrator.Status(r.JournalDirectory).Phase!="rolled-back")throw new Exception("Changed source was not rolled back");
        });
        test("source-cache-metadata-reject-before-write",dir=>{
            var r=Prepare(dir);string old=RegularImage.HashFile(r.Destination.Path);
            using(var disk=RegularImage.Open(r.Source.Image,true)){var v=new UfsVolume(disk,32);v.Inventory(false);var n=v.Nodes.Values.First(n=>n.Type==8);var raw=(byte[])n.Raw.Clone();raw[7]^=1;RegularImage.Write(disk,v.InodeOffset(n.Number),raw);RegularImage.Flush(disk);}
            ExpectFailure(()=>DirectMigrator.Run(r));
            if(old!=RegularImage.HashFile(r.Destination.Path)||Directory.Exists(r.JournalDirectory))throw new Exception("Metadata mismatch caused writes");
        });
        test("transfer-pointer-cache-eviction-and-triple",dir=>{
            var f=new Fixture(8);f.Finish(true);string path=Path.Combine(dir,"target.img");File.WriteAllBytes(path,f.Image);
            using var disk=RegularImage.Open(new(path,32),true);var v=new UfsVolume(disk,32);v.Inventory(false);v.ReleaseOldTree();
            byte[] raw=new byte[256];var cache=new TransferPointerCache(v,default);
            var logical=new List<long>();
            for(int i=0;i<40;i++)logical.Add(12L+v.Fanout+(long)i*v.Fanout);
            logical.Add(12L+v.Fanout+(long)v.Fanout*v.Fanout+3);
            var expected=new Dictionary<long,long>();
            foreach(long block in logical){long address=v.Allocate(v.Fpb);v.Write(address,new byte[v.Block]);expected.Add(block,address);v.PutPointer(raw,block,address,cache);}
            cache.Flush();UfsVolume.P64(raw,0x10,(logical.Max()+1)*v.Block);System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(raw,0x8180);
            var actual=v.NonzeroBlocks(new Node(2,raw)).ToDictionary(p=>p.Logical,p=>p.Fragment);
            if(!expected.OrderBy(p=>p.Key).SequenceEqual(actual.OrderBy(p=>p.Key)))throw new Exception("Evicted/triple pointers differ");
        });
        test("transfer-batched-partial-write-rolls-back",dir=>{
            var r=Prepare(dir);bool armed=false,fired=false;string original;
            using(var old=RegularImage.Open(r.Destination.Image)){var v=new UfsVolume(old,32);v.Inventory(true);original=DirectLayout.TreeDigest(v);}
            try{
                DirectMigrator.RunCore(r,s=>{if(s.StartsWith("Copy inode"))armed=true;},default,(e,w)=>{
                    var disk=new DirectDisk(e,w);return w?new PartialBatchFault(disk,()=>armed,()=>fired=true):disk;});
                throw new Exception("Expected batched write failure");
            }catch(IOException e)when(e.Message=="Synthetic batched partial write"){}
            if(!fired||DirectMigrator.Status(r.JournalDirectory).Phase!="rolled-back")throw new Exception("Batched rollback not exercised");
            using var result=RegularImage.Open(r.Destination.Image);var check=new UfsVolume(result,32);check.Inventory(true);
            if(DirectLayout.TreeDigest(check)!=original)throw new Exception("Batched failure changed original target tree");
        });
        foreach(string crypto in new[]{"plain","cbc192-bswap16"})test("transfer-4MiB-partial-write-rollback-"+crypto,dir=>{
            var source=new Fixture(4,ipg:32768);
            source.Add("large",payload:Fixture.Payload(12*1024*1024+731,151));source.Finish();
            var old=new Fixture(5,ipg:32768);old.Add("original",payload:Fixture.Payload(8193,152));old.Finish(true);
            var setupRequest=setup(dir,source,old,crypto,registry,registry);
            var r=new DirectRequest(new(setupRequest.Source.Path,32,crypto,setupRequest.Source.KeyFile),
                new(setupRequest.DestinationTemplate.Path,32,crypto,setupRequest.DestinationTemplate.KeyFile),
                Path.Combine(dir,"journal"),setupRequest.SourceRegistry,setupRequest.TargetRegistry);
            string sourceBefore=RegularImage.HashFile(r.Source.Path),original;
            using(var disk=RegularImage.Open(r.Destination.Image)){var v=new UfsVolume(disk,32);v.Inventory(true);original=DirectLayout.TreeDigest(v);}
            bool armed=false,fired=false;
            try{
                DirectMigrator.RunCore(r,s=>{if(s.StartsWith("Copy inode"))armed=true;},default,(e,w)=>{
                    if(e.Kind!="image")throw new Exception("Synthetic images only");
                    var disk=new DirectDisk(e,w);return w?new PartialBatchFault(disk,()=>armed,()=>fired=true,2*1024*1024):disk;});
                throw new Exception("Expected large batched partial write");
            }catch(IOException e)when(e.Message=="Synthetic batched partial write"){}
            if(!fired||DirectMigrator.Status(r.JournalDirectory).Phase!="rolled-back"||RegularImage.HashFile(r.Source.Path)!=sourceBefore)
                throw new Exception("Large-write failure did not preserve source and roll back");
            using(var result=RegularImage.Open(r.Destination.Image)){
                var check=new UfsVolume(result,32);check.Inventory(true);
                if(DirectLayout.TreeDigest(check)!=original)throw new Exception("Large-write rollback changed target tree");
                Oracle.Check(old,result.ReadBytes(0,old.Image.Length),new MigrationManifest(1,setupRequest,old.Items.Count,
                    old.Items.Select(n=>new InodeMapping(n.Ino,n.Ino)).ToList(),"",null,false));
            }
            File.Delete(r.Source.Path);File.Delete(r.Destination.Path);
        });
    }
    sealed class PartialBatchFault(IIdentifiedDisk disk,Func<bool> armed,Action fired,int minimumBytes=16384):IIdentifiedDisk
    {
        bool failed;public string Identity=>disk.Identity;public long TotalSize=>disk.TotalSize;public int SectorSize=>512;public long SectorCount=>disk.SectorCount;public string Description=>disk.Description;public bool CanWrite=>true;
        public byte[] ReadBytes(long at,int count)=>disk.ReadBytes(at,count);public byte[] ReadSectors(long at,int count)=>disk.ReadSectors(at,count);
        public void WriteBytes(long at,byte[] data)=>WriteBytes(at,data.AsSpan());
        public void WriteBytes(long at,ReadOnlySpan<byte> data){if(!failed&&armed()&&data.Length>minimumBytes){failed=true;fired();disk.WriteBytes(at,data[..512]);disk.Flush();throw new IOException("Synthetic batched partial write");}disk.WriteBytes(at,data);}
        public void WriteSectors(long at,byte[] data)=>WriteBytes(checked(at*512),data);public void Flush()=>disk.Flush();public void Dispose()=>disk.Dispose();
    }
}

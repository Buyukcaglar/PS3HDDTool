using System.Diagnostics;
using System.Text.Json;
using PS3HddTool.Core.Disk;
using PS3HddTool.Core.Migration;

static class TransferPerformanceTests
{
    internal static void Run(string directory)
    {
        var source = new Fixture(112);
        source.Add("game",4); source.Add("game/SYNTHETIC",4);
        var file=source.Add("game/SYNTHETIC/payload.bin",payload:Fixture.Payload(64*1024*1024+731,71));
        file.Ea=Fixture.Payload(17001,72);
        source.Link("game","second-name",file);
        var holes=source.Add("sparse",size:20L*16384+99);holes.Data[0]=new byte[]{1};holes.Data[20]=new byte[]{2};
        source.Add("short-link",10,payload:System.Text.Encoding.ASCII.GetBytes("game/SYNTHETIC"),inline:true);
        source.Finish();
        var template=new Fixture(160);template.Finish(true);
        string src=Path.Combine(directory,"source.img"),dst=Path.Combine(directory,"target.img"),key=Path.Combine(directory,"generated.key");
        File.WriteAllBytes(key,Fixture.Payload(24,199));
        var reports=new List<object>();
        foreach(string crypto in new[]{"plain","cbc192-bswap16"})
        {
            File.WriteAllBytes(src,source.Image);
            var srcSpec=new ImageSpec(src,32,crypto,crypto=="plain"?null:key);
            if(crypto!="plain")using(var enc=RegularImage.Open(srcSpec,true)){for(int at=0;at<source.Image.Length;at+=1024*1024)enc.WriteBytes(at,source.Image.AsSpan(at,Math.Min(1024*1024,source.Image.Length-at)));RegularImage.Flush(enc);}
            foreach(bool optimized in new[]{true,false,true})
            {
                using var sourceDisk=RegularImage.Open(srcSpec);
                var s=new UfsVolume(sourceDisk,32);s.Inventory(true);
                File.WriteAllBytes(dst,template.Image);
                using IIdentifiedDisk raw=new DirectDisk(new DirectEndpoint(dst,0),true);
                var counted=new CountedDisk(raw);
                using var targetDisk=RegularImage.WrapCrypto(new BorrowedDisk(counted),new ImageSpec(dst,32,crypto,crypto=="plain"?null:key));
                for(int at=0;at<template.Image.Length;at+=1024*1024)targetDisk.WriteBytes(at,template.Image.AsSpan(at,Math.Min(1024*1024,template.Image.Length-at)));
                RegularImage.Flush(targetDisk);
                var baseline=new UfsVolume(targetDisk,32);baseline.Inventory(false);
                var layout=DirectLayout.Plan(s,baseline,default,null);
                using var guarded=RegularImage.WrapCrypto(new JournaledTarget(counted,baseline,layout.Metadata),new ImageSpec(dst,32,crypto,crypto=="plain"?null:key));
                var d=new UfsVolume(guarded,32);d.Inventory(false);d.ReleaseOldTree(false);
                counted.Reset();var watch=Stopwatch.StartNew();
                var mapping=optimized?TreeTransfer.Copy(s,d,null,default):LegacyTreeTransfer.Copy(s,d,null,default);
                RegularImage.Flush(guarded);watch.Stop();
                var report=new{physical=false,crypto,optimized,journalWriteGuard=true,payloadBytes=source.Items.Where(n=>n.Type==8).Sum(n=>n.Size),seconds=watch.Elapsed.TotalSeconds,
                    readCalls=counted.ReadCalls,readBytes=counted.ReadBytesTotal,writeCalls=counted.WriteCalls,writeBytes=counted.WriteBytesTotal,
                    readSeconds=counted.ReadTicks/(double)Stopwatch.Frequency,writeSeconds=counted.WriteTicks/(double)Stopwatch.Frequency,maxWriteBytes=counted.MaxWrite};
                d.ReleaseRetiredData();d.RecalculateAndFlush();RegularImage.Flush(guarded);
                var check=new UfsVolume(targetDisk,32);check.Inventory(true);ImageMigrator.Compare(s,check,mapping,default);
                Oracle.Check(source,targetDisk.ReadBytes(0,template.Image.Length),new MigrationManifest(1,new(srcSpec,new ImageSpec(dst,32),directory),source.Items.Count,mapping.Select(p=>new InodeMapping(p.Key,p.Value)).ToList(),"",null,false));
                reports.Add(report);Console.WriteLine(JsonSerializer.Serialize(report));
            }
        }
        File.WriteAllBytes(Path.Combine(directory,"performance.json"),JsonSerializer.SerializeToUtf8Bytes(reports,ImageMigrator.Json));
    }
    sealed class CountedDisk(IIdentifiedDisk raw):IIdentifiedDisk
    {
        public long ReadCalls,WriteCalls,ReadBytesTotal,WriteBytesTotal,ReadTicks,WriteTicks;public int MaxWrite;
        public void Reset(){ReadCalls=WriteCalls=ReadBytesTotal=WriteBytesTotal=ReadTicks=WriteTicks=0;MaxWrite=0;}
        public long TotalSize=>raw.TotalSize;public int SectorSize=>512;public long SectorCount=>raw.SectorCount;public string Identity=>raw.Identity;public string Description=>raw.Description;public bool CanWrite=>raw.CanWrite;
        public byte[] ReadBytes(long at,int length){long start=Stopwatch.GetTimestamp();var b=raw.ReadBytes(at,length);ReadTicks+=Stopwatch.GetTimestamp()-start;ReadCalls++;ReadBytesTotal+=length;return b;}
        public byte[] ReadSectors(long at,int count)=>ReadBytes(checked(at*512),checked(count*512));
        public void WriteBytes(long at,byte[] bytes)=>WriteBytes(at,bytes.AsSpan());
        public void WriteBytes(long at,ReadOnlySpan<byte> bytes){long start=Stopwatch.GetTimestamp();raw.WriteBytes(at,bytes);WriteTicks+=Stopwatch.GetTimestamp()-start;WriteCalls++;WriteBytesTotal+=bytes.Length;MaxWrite=Math.Max(MaxWrite,bytes.Length);}
        public void WriteSectors(long at,byte[] bytes)=>WriteBytes(checked(at*512),bytes);public void Flush()=>raw.Flush();public void Dispose(){}
    }
}

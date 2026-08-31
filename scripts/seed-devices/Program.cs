using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Services;
using MainAPP.Services;

const int count = 8;
var settings = new AppSettings();
var repo = new DeviceRepository(settings);
var path = repo.FilePath;

if (File.Exists(path))
{
    var backup = path + $".bak-{DateTime.Now:yyyyMMddHHmmss}";
    File.Copy(path, backup, overwrite: true);
    Console.WriteLine($"Backed up: {backup}");
}

var samples = SampleDeviceBuilder.BuildSampleDevices().Take(count).ToList();
for (var i = 0; i < samples.Count; i++)
    samples[i].Id = $"device-{i + 1:D3}";

repo.ReplaceAllAndSave(samples);
Console.WriteLine($"Wrote {samples.Count} devices to {path}");
foreach (var d in samples)
    Console.WriteLine($"  {d.Id}  {d.Name}  OK={d.OkCountAddress}");

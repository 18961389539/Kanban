using System.Collections.Generic;
using System.ComponentModel;
using Kanban.Collector.Core.Models;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class DataSourceModelTests
{
    [Fact]
    public void DisplayName_NotifiesWhenNameOrTriggerChanges()
    {
        var source = new DataSource { Name = "温度" };
        var changed = new List<string?>();
        source.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.Equal("温度（定时采集）", source.DisplayName);

        source.Name = "压力";
        Assert.Contains(nameof(DataSource.DisplayName), changed);
        Assert.Equal("压力（定时采集）", source.DisplayName);

        changed.Clear();
        source.TriggerAddress = "D510";
        Assert.Contains(nameof(DataSource.HasTrigger), changed);
        Assert.Contains(nameof(DataSource.DisplayName), changed);
        Assert.Equal("压力", source.DisplayName);
    }
}
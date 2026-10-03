using System.Reflection;
using Microsoft.AspNetCore.Components;
using SchedularrTV.Service.Components.Shared;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class ArchiveMappingListTests
{
    [Fact]
    public void Unmapped_entries_precede_mapped_entries_with_frequency_then_name_order_in_each_group()
    {
        var component = new MappingProbe();
        component.Configure(new Dictionary<string, object?>
        {
            [nameof(ArchiveMappingList.Names)] = new[] { "Mapped", "Rare", "Frequent", "Auto", "Excluded", "Unavailable", "Tie B", "Tie A" },
            [nameof(ArchiveMappingList.Targets)] = new[] { "Library title" },
            [nameof(ArchiveMappingList.Assignments)] = new Dictionary<string, string?>
            {
                ["Mapped"] = "Library title", ["Excluded"] = null, ["Unavailable"] = "Deleted title"
            },
            [nameof(ArchiveMappingList.Automatic)] = new Func<string, string?>(name => name is "Auto" or "Excluded" ? "Library title" : null),
            [nameof(ArchiveMappingList.Counts)] = new Dictionary<string, long>
            {
                ["Mapped"] = 1000, ["Auto"] = 2000, ["Rare"] = 1, ["Frequent"] = 50,
                ["Excluded"] = 100, ["Unavailable"] = 20, ["Tie A"] = 5, ["Tie B"] = 5
            }
        });
        Assert.Equal(["Excluded", "Frequent", "Unavailable", "Tie A", "Tie B", "Rare", "Auto", "Mapped"], component.OrderedNames);
    }

    [Fact]
    public void Assignment_changes_move_entries_between_groups_without_losing_counts()
    {
        Dictionary<string, string?> assignments = [];
        var component = new MappingProbe();
        var parameters = new Dictionary<string, object?>
        {
            [nameof(ArchiveMappingList.Names)] = new[] { "Frequent", "Rare", "No scan count" },
            [nameof(ArchiveMappingList.Targets)] = new[] { "Target" },
            [nameof(ArchiveMappingList.Assignments)] = assignments,
            [nameof(ArchiveMappingList.Counts)] = new Dictionary<string, long> { ["Frequent"] = 20, ["Rare"] = 1 }
        };
        component.Configure(parameters);
        Assert.Equal(["Frequent", "Rare", "No scan count"], component.OrderedNames);
        assignments["Frequent"] = "Target";
        component.Configure(parameters);
        Assert.Equal(["Rare", "No scan count", "Frequent"], component.OrderedNames);
        assignments["Frequent"] = null;
        component.Configure(parameters);
        Assert.Equal(["Frequent", "Rare", "No scan count"], component.OrderedNames);
    }

    private sealed class MappingProbe : ArchiveMappingList
    {
        public IReadOnlyList<string> OrderedNames => (IReadOnlyList<string>)typeof(ArchiveMappingList)
            .GetField("_filtered", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;

        public void Configure(Dictionary<string, object?> parameters)
        {
            ParameterView.FromDictionary(parameters).SetParameterProperties(this);
            OnParametersSet();
        }
    }
}
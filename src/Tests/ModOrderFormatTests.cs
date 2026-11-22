using ModManager.Models.Mod.Order;
using ModManager.Util;

using Xunit;

namespace ModManager.Tests;

public class ModOrderFormatTests(ITestOutputHelper output) : BaseTest(output)
{
	[Fact]
	public void LegacyV1Order_WithUuidEntries_Loads()
	{
		// Format written by BG3ModManager 1.x (the WPF version).
		const string json = """
		{
		  "Order": [
		    { "UUID": "755a8a72-407f-4f0d-9a33-274ac0f0b53d", "Name": "Mod Configuration Menu" },
		    { "UUID": "26922ba9-6018-5252-075d-7ff2ba6ed879", "Name": "ImpUI (ImprovedUI)" }
		  ]
		}
		""";
		var order = JsonUtils.SafeDeserialize<ModOrder>(json);
		Assert.NotNull(order);
		Assert.Equal(["755a8a72-407f-4f0d-9a33-274ac0f0b53d", "26922ba9-6018-5252-075d-7ff2ba6ed879"], order!.Entries.Select(x => x.Id));
		Assert.Equal("ImpUI (ImprovedUI)", order.Entries[1].Name);
	}

	[Fact]
	public void CurrentOrderFormat_StillLoads()
	{
		const string json = """
		{ "Entries": [ { "Id": "cb555efe-2d9e-131f-8195-a89329d218ea", "Name": "GustavX" } ] }
		""";
		var order = JsonUtils.SafeDeserialize<ModOrder>(json);
		Assert.Equal("cb555efe-2d9e-131f-8195-a89329d218ea", Assert.Single(order!.Entries).Id);
	}
}

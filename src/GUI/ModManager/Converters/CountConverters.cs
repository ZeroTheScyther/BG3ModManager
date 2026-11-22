using Avalonia.Data.Converters;

namespace ModManager.Converters;

public static class CountConverters
{
	/// <summary>True when an item count is greater than one, e.g. to hide a selector with nothing to choose between.</summary>
	public static readonly IValueConverter GreaterThanOne = new FuncValueConverter<int, bool>(count => count > 1);
}

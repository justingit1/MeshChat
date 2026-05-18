using System.Globalization;
using System.Windows.Media;
using MeshChat.Converters;

namespace MeshChat.Tests;

public sealed class AvatarGradientConverterTests
{
    [Fact]
    public void Convert_SamePeerId_ReturnsSameGradientInProcess()
    {
        var converter = new AvatarGradientConverter();

        var first = converter.Convert("peer-123", typeof(Brush), parameter: null!, CultureInfo.InvariantCulture);
        var second = converter.Convert("peer-123", typeof(Brush), parameter: null!, CultureInfo.InvariantCulture);

        Assert.Same(first, second);
    }

    [Fact]
    public void Implementation_DoesNotUseStringGetHashCode()
    {
        var source = File.ReadAllText(FindSourceFile("Converters.cs"));
        var converterStart = source.IndexOf("public class AvatarGradientConverter", StringComparison.Ordinal);
        Assert.True(converterStart >= 0);

        var nextClass = source.IndexOf("public class", converterStart + 1, StringComparison.Ordinal);
        var converterSource = nextClass < 0
            ? source[converterStart..]
            : source[converterStart..nextClass];

        Assert.DoesNotContain(".GetHashCode(", converterSource, StringComparison.Ordinal);
    }

    private static string FindSourceFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var path = Path.Combine(directory.FullName, fileName);
            if (File.Exists(path))
                return path;

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {fileName} from {AppContext.BaseDirectory}.");
    }
}

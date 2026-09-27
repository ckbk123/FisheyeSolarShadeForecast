using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using SolarShade.Desktop;
using Xunit;

public class PortableTests
{
    [Fact]
    public void InternalPathsFollowPackageWhileExternalPathsStayAbsolute()
    {
        string oldRoot = @"C:\Packages\Original", newRoot = @"D:\Moved package";
        var original = new UserSettings
        {
            CalibrationFolder = oldRoot + @"\Example\Calib Images", SkyFolder = oldRoot + @"\Example\Sky Photo",
            SkyImage = oldRoot + @"\Example\Sky Photo\sky.jpg", ProfilePath = oldRoot + @"\Data\calibrations\profile.json",
            ImportPath = @"C:\Users\Someone\own-weather.xlsx"
        };
        var saved = PortablePaths.Map(original, p => PortablePaths.Store(p, oldRoot));
        Assert.Equal("Example/Calib Images", saved.CalibrationFolder);
        Assert.Equal("Data/calibrations/profile.json", saved.ProfilePath);
        Assert.Equal(original.ImportPath, saved.ImportPath);
        var moved = PortablePaths.Map(saved, p => PortablePaths.Resolve(p, newRoot));
        Assert.Equal(newRoot + @"\Example\Sky Photo\sky.jpg", moved.SkyImage);
        Assert.Equal(newRoot + @"\Data\calibrations\profile.json", moved.ProfilePath);
        Assert.Equal(original.ImportPath, moved.ImportPath);
        Assert.Equal(@"C:\Packages\Original-other\photo.jpg", PortablePaths.Store(@"C:\Packages\Original-other\photo.jpg", oldRoot));
        Assert.Equal("", PortablePaths.Resolve("", newRoot));
    }

    [Fact]
    public void RelativeInputUsesExecutableDirectoryInsteadOfWorkingDirectory()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Example", "Sky Photo", "sky.jpg"), PortablePaths.Resolve("Example/Sky Photo/sky.jpg"));
    }

}

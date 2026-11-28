using System.Windows;
using System.Windows.Controls;

namespace SolarShade.Desktop;

public sealed partial class IrradianceWorkspace
{
    private const string NoMaskHelp = "Click Update results to use an AI mask, or select or import another mask for this photo.";
    private bool removingMask;
    private readonly Button removeMask = new() { Content = "Remove mask", Margin = new(0, 4, 0, 4) };
    private readonly Border noMaskStamp = new()
    {
        Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center, BorderThickness = new(2), CornerRadius = new(3),
        BorderBrush = StudioTheme.Muted, Padding = new(12, 5, 12, 5), Focusable = true,
        Child = new TextBlock { Text = "NO MASK", Foreground = StudioTheme.Muted, FontSize = 20, FontWeight = FontWeights.SemiBold }
    };

    internal async Task RemoveSelectedMask()
    {
        if (removingMask || !removeMask.IsEnabled || !CanImportForCurrentPhoto || settings.ActiveSkyPhoto is not { HasDigest: true } photo) return;
        var selection = settings with { };
        removingMask = true;
        IsEnabled = Actions.IsEnabled = false;
        try
        {
            await InputsSettled;
            await services.RemoveMaskAsync(selection, photo.Content);
            settings.SelectedMaskId = null;
            settings.NoMaskSelected = true;
            settings.LastMaskByPhoto = new(settings.LastMaskByPhoto);
            settings.LastMaskByPhoto.Remove(photo.Content);
            ClearPreviousPhotoResults();
            Changed(); RefreshMaskChoices(); ShowSelectedMaskPreview();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Changed(refreshSources: true, verifyArtifacts: true);
            ShowSelectedMaskPreview();
            ShowError(ex);
        }
        finally
        {
            removingMask = false;
            if (!closing) { IsEnabled = Actions.IsEnabled = true; RefreshButtons(); }
        }
    }

    private void AcceptGeneratedSelection()
    {
        if (!settings.NoMaskSelected) return;
        settings.NoMaskSelected = false;
        if (updating) uiInputs = InputDependencies.Capture(settings, uiInputs, false);
        RefreshMaskChoices();
        SaveInputs();
    }
}

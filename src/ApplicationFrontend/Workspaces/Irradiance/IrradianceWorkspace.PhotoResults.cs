using System.Windows;

namespace SolarShade.Desktop;

public sealed partial class IrradianceWorkspace
{
    private void ClearPreviousPhotoResults()
    {
        controller.DiscardPhotoResult();
        chart.SetData([], TimeZoneInfo.Utc);
        graphDay.SelectedDate = null; graphDay.DisplayDateStart = null; graphDay.DisplayDateEnd = null;
        beforeEnergy.Text = afterEnergy.Text = reduction.Text = "—";
        beforeEnergy.Opacity = afterEnergy.Opacity = reduction.Opacity = chart.Opacity = 1;
        runDetails.Text = modelDetails.Text = lastWeatherResult.Text = "";
        studyDetails.Visibility = provenance.Visibility = Visibility.Collapsed;
        export.IsEnabled = false;
    }
}

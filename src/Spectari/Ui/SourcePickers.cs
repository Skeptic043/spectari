namespace Spectari.Ui;

/// <summary>Fills source and audio dropdowns from a source selection model.</summary>
internal static class SourcePickers
{
    internal static void RenderSourcePickers(
        SourceSelectionModel sourceSelection,
        ComboBox windowCombo,
        ComboBox monitorCombo,
        ComboBox captureDeviceCombo,
        ComboBox audioCombo)
    {
        RenderPicker(windowCombo, sourceSelection.WindowDisplayItems, sourceSelection.SelectedWindowIndex);
        RenderPicker(monitorCombo, sourceSelection.MonitorDisplayItems, sourceSelection.SelectedMonitorIndex);
        RenderPicker(
            captureDeviceCombo,
            sourceSelection.CaptureDeviceDisplayItems,
            sourceSelection.SelectedCaptureDeviceIndex);
        RenderAudioPicker(sourceSelection, audioCombo);
    }

    internal static void RenderAudioPicker(SourceSelectionModel sourceSelection, ComboBox audioCombo) =>
        RenderPicker(audioCombo, sourceSelection.AudioDisplayItems, sourceSelection.SelectedAudioIndex);

    internal static void RenderPicker(ComboBox combo, IReadOnlyList<string> items, int selectedIndex)
    {
        combo.BeginUpdate();
        combo.Items.Clear();
        foreach (string item in items) combo.Items.Add(item);
        combo.SelectedIndex = selectedIndex >= 0 && selectedIndex < items.Count ? selectedIndex : -1;
        combo.EndUpdate();
    }

    internal static int PickerRowHeight(ComboBox combo) =>
        combo.PreferredHeight + combo.Margin.Vertical;
}

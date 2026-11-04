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

    /// <summary>
    /// Applies the user's pick in an audio dropdown. The "leaving out an app"
    /// entry first asks which app; canceling keeps the previous choice.
    /// </summary>
    internal static void CommitAudioChoice(IWin32Window owner, SourceSelectionModel sources, ComboBox audioCombo)
    {
        if (!sources.IsDesktopExceptIndex(audioCombo.SelectedIndex))
        {
            sources.SelectAudioIndex(audioCombo.SelectedIndex);
            return;
        }
        LeaveOutAppDialog.Choose(owner, sources);
        RenderAudioPicker(sources, audioCombo);
    }

    internal static int PickerRowHeight(ComboBox combo) =>
        combo.PreferredHeight + combo.Margin.Vertical;
}

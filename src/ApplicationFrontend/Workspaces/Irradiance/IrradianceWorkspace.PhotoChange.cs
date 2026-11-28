namespace SolarShade.Desktop;

public sealed partial class IrradianceWorkspace
{
    private const string PhotoSwitchHelp = "Choosing a different sky photo removes the previous photo’s generated results and mask edits.";
    private bool photoChanging, photoCleanupFailed, photoUnavailable;

    private async Task WaitForInputWork()
    {
        // A completed switch can queue one more observation for the incoming photo.
        Task work;
        do { work = inputWork; await work; } while (work != inputWork);
    }

    private bool ResumePhotoSelection(SourceIdentity current)
    {
        if (string.IsNullOrWhiteSpace(settings.SkyImage)) return false;
        if (settings.ActiveSkyPhoto == null)
        {
            // Capture the previous identity before a missing file can invalidate its last recorded run.
            string? prior = settings.SelectedMaskId == null ? null : settings.LastMaskByPhoto
                .FirstOrDefault(pair => pair.Value == settings.SelectedMaskId).Key;
            if (!PhotoStorageCleanup.IsHash(prior)) prior = settings.ApparentFovSources?.PhotoSha256;
            if (!PhotoStorageCleanup.IsHash(prior)) prior = services.LastRecordedSkyPhoto(settings.SkyImage)?.Content;
            if (prior == null && !current.HasDigest) return false;
            settings.ActiveSkyPhoto = new(settings.SkyImage, prior ?? current.Content);
            SaveInputs();
        }
        if (!current.HasDigest)
        {
            HoldUnavailablePhoto();
            return true;
        }
        if (settings.ActiveSkyPhoto.Content == current.Content) return false;
        StartPhotoChange(settings with { }, current);
        return true;
    }

    private static SourceIdentity? ChangedPhoto(UserSettings study)
    {
        if (study.ActiveSkyPhoto == null || string.IsNullOrEmpty(study.SkyImage)) return null;
        var current = SourceIdentity.Read(study.SkyImage, null, true);
        return current.Content != study.ActiveSkyPhoto.Content ? current : null;
    }

    private void HoldUnavailablePhoto()
    {
        if (!photoUnavailable)
        {
            photoUnavailable = true;
            revision++; pending?.Cancel();
            ClearPreviousPhotoResults();
            ApplyInvalidation(ArtifactGroup.Mask | ArtifactGroup.Profile | InputDependencies.PoseChain);
        }
        SetUpdateState(UpdateState.NeedsAttention, "The sky photograph is unavailable or incomplete. Choose a readable photo or finish copying the file.");
    }

    private void SelectPhotoStudy(UserSettings next)
    {
        using var pixels = ReadPhoto(next.SkyImage, out string hash);
        if (pixels.Empty()) throw new IOException("Choose a complete, readable sky photograph.");
        var image = new SourceIdentity(PortablePaths.Resolve(next.SkyImage), hash);
        if (photoCleanupFailed && settings.ActiveSkyPhoto?.Content == image.Content)
            throw new IOException("Finish the pending photo change before selecting the previous photo again.");
        if (settings.ActiveSkyPhoto is { } active && active.Content != image.Content)
        {
            StartPhotoChange(next, image, decoded: true);
            return;
        }
        next.ActiveSkyPhoto = new(next.SkyImage, image.Content);
        photoUnavailable = false;
        settings = next;
        RestorePhotoMaskSelection(); LoadFields(); Changed(); RefreshMaskChoices(); ShowSelectedMaskPreview();
    }

    private bool StartPhotoChange(UserSettings next, SourceIdentity target, bool decoded = false)
    {
        if (photoChanging || closing || settings.ActiveSkyPhoto is not { HasDigest: true } previous)
            return false;
        if (!target.HasDigest) { HoldUnavailablePhoto(); return false; }
        try
        {
            if (!decoded)
            {
                using var pixels = ReadPhoto(next.SkyImage, out string readHash);
                if (pixels.Empty() || readHash != target.Content) throw new IOException("Choose a complete, readable sky photograph.");
            }
            // A source selected from the live debug folder must be preserved before those outputs are discarded.
            next = services.PrepareInputs(next);
            next.ActiveSkyPhoto = previous;
            next.SetRefinedEdge(null);
            next.LastMaskByPhoto = new(next.LastMaskByPhoto);
            next.LastMaskByPhoto.Remove(previous.Content);
            next.SelectedMaskId = null; next.NoMaskSelected = false;
            // Writing the new source together with the outgoing identity makes an interrupted cleanup retryable.
            AppData.SaveSettings(next);
            settings = next;
            photoChanging = true; photoCleanupFailed = photoUnavailable = false;
            revision++; pending?.Cancel();
            ClearPreviousPhotoResults();
            ApplyInvalidation(ArtifactGroup.Mask | ArtifactGroup.Profile | InputDependencies.PoseChain);
            LoadFields();
            IsEnabled = Actions.IsEnabled = false;
            SetUpdateState(UpdateState.UpdateRequired, "Clearing the previous photo…");
            inputWork = CompletePhotoChange(inputWork, previous, target, next);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (photoChanging || photoCleanupFailed)
            {
                photoChanging = false; photoCleanupFailed = true;
                IsEnabled = Actions.IsEnabled = true;
                ShowError(ex);
            }
            else if (string.Equals(PortablePaths.Resolve(previous.Path), PortablePaths.Resolve(next.SkyImage), StringComparison.OrdinalIgnoreCase))
                HoldUnavailablePhoto();
            else
            {
                LoadFields();
                detail.Text = ex.Message;
            }
            return false;
        }
    }

    private async Task CompletePhotoChange(Task preceding, SourceIdentity previous, SourceIdentity target, UserSettings next)
    {
        await Task.Yield();
        try
        {
            await preceding;
            // The import dialog reports its own failures. Wait until it can no longer write an outgoing variant.
            try { await pendingMaskImport; } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            var incoming = new SourceIdentity(next.SkyImage, target.Content);
            await services.DiscardPhotoAsync(previous, next with { ActiveSkyPhoto = incoming }, target.Content);
            settings.ActiveSkyPhoto = incoming;
            RestorePhotoMaskSelection();
            AppData.SaveSettings(settings);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            photoCleanupFailed = true;
            settings.ActiveSkyPhoto = previous;
            if (!closing) ShowError(new IOException("Photo change is incomplete. Close any open generated files, then choose Update results to retry. " + ex.Message, ex));
        }
        finally
        {
            photoChanging = false;
            if (!closing) IsEnabled = Actions.IsEnabled = true;
        }
        if (closing || photoCleanupFailed) return;
        uiInputs = InputDependencies.Capture(settings);
        LoadFields(); RefreshInputState(); Changed(refreshSources: true); ShowSelectedMaskPreview();
    }
}

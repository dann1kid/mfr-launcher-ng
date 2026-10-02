using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mfr.Core.Exceptions;
using Mfr.Core.Storage;
using Mfr.Core.Tasks;
using Mfr.Protocol.Dto;
using Mfr.Protocol.Enums;
using Section = Mfr.Core.Storage.Section;

namespace Mfr.Launcher.ViewModels;

/// <summary>
/// Game options screen (Kotlin: GameController): sections and extra packs on
/// the left (each downloadable/removable), options of the selected section as
/// radio buttons on the right with description and preview image, and a
/// save button that applies the changed options.
/// </summary>
public sealed partial class GameOptionsViewModel : ObservableObject
{
    private readonly LauncherServices _services;
    private readonly MainViewModel _owner;
    private readonly Action _close;

    public GameOptionsViewModel(LauncherServices services, MainViewModel owner, Action close)
    {
        _services = services;
        _owner = owner;
        _close = close;
        foreach (var section in services.Repository.GetSections().OrderBy(s => s.Name))
        {
            Sections.Add(new SectionRowViewModel(section, services, this));
        }
        foreach (var extra in services.Repository.GetExtras().OrderBy(e => e.Name))
        {
            Extras.Add(new ExtraRowViewModel(extra, services, this));
        }
        SelectedSection = Sections.FirstOrDefault();
        ApplyButtonEnabled = false;
    }

    public ObservableCollection<SectionRowViewModel> Sections { get; } = [];

    public ObservableCollection<ExtraRowViewModel> Extras { get; } = [];

    [ObservableProperty]
    private SectionRowViewModel? _selectedSection;

    [ObservableProperty]
    private bool _applyButtonEnabled;

    [ObservableProperty]
    private int _percent;

    [ObservableProperty]
    private string _progressDescription = "";

    [ObservableProperty]
    private bool _progressVisible;

    partial void OnSelectedSectionChanged(SectionRowViewModel? value)
    {
        foreach (var section in Sections.Where(s => s != value && s.IsSectionSelected))
        {
            section.IsSectionSelected = false;
        }
        if (value is { IsSectionSelected: false })
        {
            value.IsSectionSelected = true;
        }
        OnPropertyChanged(nameof(Options));
        OnPropertyChanged(nameof(SelectedOptionDescription));
        OnPropertyChanged(nameof(SelectedOptionImage));
    }

    internal void SelectSection(SectionRowViewModel section)
    {
        if (SelectedSection != section)
        {
            SelectedSection = section;
        }
    }

    public ObservableCollection<OptionRowViewModel> Options => SelectedSection?.Options ?? [];

    public string SelectedOptionDescription =>
        Options.FirstOrDefault(o => o.IsSelectedByUser)?.Description ?? "";

    public string? SelectedOptionImage => SelectedSection?.SelectedImage;

    /// <summary>Sections whose option selection changed since the screen opened.</summary>
    internal System.Collections.Generic.HashSet<int> OptionsForApply { get; } = [];

    internal void MarkDirty()
    {
        ApplyButtonEnabled = true;
        OnPropertyChanged(nameof(SelectedOptionDescription));
        OnPropertyChanged(nameof(SelectedOptionImage));
    }

    [RelayCommand]
    private async Task Apply()
    {
        ApplyButtonEnabled = false;
        var saved = _services.Repository.GetSections();
        var pairs = new System.Collections.Generic.List<(Option? Current, Option Target)>();
        var skipped = new System.Collections.Generic.List<string>();
        foreach (var section in saved)
        {
            var target = section.Options.FirstOrDefault(o => OptionsForApply.Contains(o.Id));
            if (target == null)
            {
                continue;
            }
            if (!section.Downloaded)
            {
                skipped.Add(section.Name);
                continue;
            }
            pairs.Add((section.Options.FirstOrDefault(o => o.Applied), target));
        }
        OptionsForApply.Clear();
        SelectedSection = null; // drop UI references to stale entities

        if (skipped.Count > 0)
        {
            var message = new Views.MessageWindow("Внимание",
                "Часть опций не будет применена, т.к. пакет опций не был загружен.\n" +
                "Пропущены следующие опции: " + string.Join(", ", skipped));
            _ = message.ShowDialog(_owner.Window!);
        }

        if (pairs.Count > 0)
        {
            await RunWithProgress(new ApplyOptionsTask(_services), task => task.Execute(pairs));
        }
        _close();
    }

    /// <summary>Downloads or deletes a section/extra pack (the small round buttons).</summary>
    internal async Task ToggleContent(RowViewModel row)
    {
        if (row.Downloaded)
        {
            // remove: delete every file of the pack
            var files = row.AllFilePaths;
            var index = 0;
            foreach (var file in files)
            {
                TryDelete(_services.Paths.Resolve(file));
                index++;
                SetProgress(index, files.Count, "Удаление файлов");
            }
            row.CommitDownloaded(false);
            _services.Repository.SetProperty(PropertyKeys.LastUpdateDate, null);
        }
        else
        {
            var content = await _services.Api.GetGameContent(_owner.CurrentBuildId);
            var category = content.Categories.FirstOrDefault(c => c.Type == row.ContentType);
            var files = category?.Items.FirstOrDefault(i => i.Name == row.Name)?.Files ?? [];
            if (files.Count == 0)
            {
                await new Views.MessageWindow("Внимание",
                    $"Пакет «{row.Name}» не найден на сервере (возможно, идёт техническое обслуживание).").ShowDialog(_owner.Window!);
                return;
            }
            await RunWithProgress(new DownloadFilesTask(_services), task => task.Execute(files));
            row.CommitDownloaded(true);
        }
        // the schema may re-detect applied options after file changes
        await new FillSchemaTask(_services).Execute(null);
        RefreshRows();
    }

    private void RefreshRows()
    {
        var sections = _services.Repository.GetSections().OrderBy(s => s.Name).ToList();
        var extras = _services.Repository.GetExtras().OrderBy(e => e.Name).ToList();
        Dispatcher.UIThread.Post(() =>
        {
            Sections.Clear();
            foreach (var section in sections)
            {
                Sections.Add(new SectionRowViewModel(section, _services, this));
            }
            Extras.Clear();
            foreach (var extra in extras)
            {
                Extras.Add(new ExtraRowViewModel(extra, _services, this));
            }
            SelectedSection = Sections.FirstOrDefault();
            ApplyButtonEnabled = false;
        });
    }

    private async Task RunWithProgress<TParam>(LauncherTask<TParam, object?> task, Func<LauncherTask<TParam, object?>, Task> run)
    {
        ProgressVisible = true;
        Percent = 0;
        task.ProgressChanged += p => Dispatcher.UIThread.Post(() => Percent = p);
        task.DescriptionChanged += d => Dispatcher.UIThread.Post(() => ProgressDescription = d);
        try
        {
            await run(task);
        }
        catch (Exception exception) when (exception is not ServerMaintenanceException)
        {
            var message = new Views.MessageWindow("Ошибка",
                exception is TaskExecuteException or DownloadFileException
                    ? "Произошла ошибка при выполнении операции"
                    : exception.Message);
            _ = message.ShowDialog(_owner.Window!);
        }
        finally
        {
            ProgressVisible = false;
        }
    }

    internal void SetProgress(long current, long total, string description)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ProgressVisible = true;
            ProgressDescription = description;
            Percent = total == 0 ? 0 : (int)(current * 100 / total);
        });
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (System.IO.IOException)
        {
        }
    }
}

public abstract partial class RowViewModel : ObservableObject
{
    protected RowViewModel(string name, bool downloaded, ContentType contentType, LauncherServices services, GameOptionsViewModel owner)
    {
        Name = name;
        Downloaded = downloaded;
        ContentType = contentType;
        Services = services;
        Owner = owner;
    }

    public string Name { get; }

    public abstract System.Collections.Generic.IReadOnlyList<string> AllFilePaths { get; }

    public ContentType ContentType { get; }

    internal LauncherServices Services { get; }

    internal GameOptionsViewModel Owner { get; }

    [ObservableProperty]
    private bool _downloaded;

    public abstract void CommitDownloaded(bool value);

    [RelayCommand]
    private Task Toggle() => Owner.ToggleContent(this);
}

public sealed partial class SectionRowViewModel : RowViewModel
{
    public SectionRowViewModel(Section section, LauncherServices services, GameOptionsViewModel owner)
        : base(section.Name, section.Downloaded, ContentType.OPTIONAL, services, owner)
    {
        Section = section;
        foreach (var option in section.Options.OrderBy(o => o.Name))
        {
            Options.Add(new OptionRowViewModel(option, this));
        }
    }

    public Section Section { get; }

    public ObservableCollection<OptionRowViewModel> Options { get; } = [];

    public string? SelectedImage => Options.FirstOrDefault(o => o.IsSelectedByUser)?.ImagePath;

    [ObservableProperty]
    private bool _isSectionSelected;

    partial void OnIsSectionSelectedChanged(bool value)
    {
        if (value)
        {
            Owner.SelectSection(this);
        }
    }

    public override System.Collections.Generic.IReadOnlyList<string> AllFilePaths =>
        Section.Options.SelectMany(o => o.Files).Select(f => f.GamePath).ToList();

    public override void CommitDownloaded(bool value)
    {
        Services.Repository.SetSectionDownloaded(Section.Id, value);
    }
}

public sealed partial class ExtraRowViewModel : RowViewModel
{
    private readonly Extra _extra;

    public ExtraRowViewModel(Extra extra, LauncherServices services, GameOptionsViewModel owner)
        : base(extra.Name, extra.Downloaded, ContentType.EXTRA, services, owner)
    {
        _extra = extra;
    }

    public override System.Collections.Generic.IReadOnlyList<string> AllFilePaths =>
        _extra.Files.Select(f => f.Path).ToList();

    public override void CommitDownloaded(bool value)
    {
        Services.Repository.SetExtraDownloaded(_extra.Id, value);
    }
}

public sealed partial class OptionRowViewModel(Option option, SectionRowViewModel sectionRow) : ObservableObject
{
    public Option Option { get; } = option;

    public string Name => Option.Name;

    public string Description => Option.Description;

    /// <summary>Image path relative to the game folder, if the option defines one.</summary>
    public string? ImagePath => Option.ImagePath is { } image
        ? sectionRow.Services.Paths.Resolve(image)
        : null;    [ObservableProperty]
    private bool _isSelectedByUser = option.Applied;

    partial void OnIsSelectedByUserChanged(bool value)
    {
        if (value)
        {
            foreach (var other in sectionRow.Options.Where(o => o != this))
            {
                other.IsSelectedByUser = false;
            }
        }
    }

    /// <summary>
    /// The old client enabled "Сохранить изменения" on ANY click, including the
    /// already selected option (re-applying is a valid case), so this runs on
    /// click rather than on selection change.
    /// </summary>
    [RelayCommand]
    private void Select()
    {
        IsSelectedByUser = true;
        sectionRow.Owner.OptionsForApply.Add(Option.Id);
        sectionRow.Owner.MarkDirty();
    }
}

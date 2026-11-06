using System.Collections.ObjectModel;
using NexusSSH.Core;
using NexusSSH.Core.Models;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;
using WinRT;

// Native AOT: WinUI bekommt Listen als COM-Objekte. Den Code dafür erzeugt CsWinRT beim Übersetzen – aber nur für
// Typen, die es in diesem Projekt sieht. Die Listen der App entstehen in NexusSSH.Core (plattformfrei, ohne WinRT);
// deshalb stehen hier alle Listentypen, die als ItemsSource an XAML gehen. Fehlt einer, wirft die Zuweisung unter
// AOT „Value does not fall within the expected range“ – im gewöhnlichen Build fällt das nicht auf.
// Neue Liste an XAML = eine Zeile hier, und in Core eine echte List<T> (BindableList.Of) oder ObservableCollection<T>.

// Laufend geänderte Listen
[assembly: GeneratedWinRTExposedExternalType(typeof(ObservableCollection<SessionViewModel>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ObservableCollection<LogEntry>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ObservableCollection<TransferItemViewModel>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ObservableCollection<SearchHit>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ObservableCollection<RemoteEntry>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ObservableCollection<PathSegment>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ObservableCollection<LocalEntry>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ObservableCollection<DirectoryNode>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ObservableCollection<FileHit>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ObservableCollection<TunnelRow>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ObservableCollection<KnownHost>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ObservableCollection<KeyFile>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ObservableCollection<ConnectionRecord>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ObservableCollection<SnippetRow>))]

// Listen, die als Ganzes ersetzt werden
[assembly: GeneratedWinRTExposedExternalType(typeof(List<ProfileGroup>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ProfileGroup))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<ServerProfile>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<PaletteItem>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<TerminalTheme>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<AccentColor>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<JumpChoice>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<AddressFamilyChoice>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<IdentityChoice>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<LinkChoice>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<ThemeChoice>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<LocalShell>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<CoreLoad>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<DiskUsage>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<NetInterface>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<DiskIoDevice>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<ServerLogLine>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<Identity>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<SavedSync>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<ShortcutRow>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<ImportCandidate>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<Snippet>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<SnippetItem>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<HomeCrumb>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<string>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<NexusSSH.Core.Models.MuxSession>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<NexusSSH.Core.Services.HelpTopic>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<NexusSSH.Core.Services.HelpShortcut>))]

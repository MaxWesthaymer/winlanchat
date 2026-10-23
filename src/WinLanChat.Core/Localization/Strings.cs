using System.Globalization;
using System.Resources;

namespace WinLanChat.Core.Localization;

/// <summary>Shared application text. The saved language is independent of the Windows language.</summary>
public static class Strings
{
    private static readonly ResourceManager Russian = new(
        "WinLanChat.Core.Localization.russian", typeof(Strings).Assembly);
    private static readonly ResourceManager English = new(
        "WinLanChat.Core.Localization.english", typeof(Strings).Assembly);
    public static string LanguageCode { get; private set; } = GetDefaultLanguage();
    public static event Action? LanguageChanged;

    public static string GetDefaultLanguage() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";

    public static bool IsSupportedLanguage(string? code) => code is "ru" or "en";

    public static void SetLanguage(string code)
    {
        if (!IsSupportedLanguage(code))
            throw new ArgumentException("Unsupported language code.", nameof(code));
        if (LanguageCode == code)
            return;
        LanguageCode = code;
        LanguageChanged?.Invoke();
    }

    public static string Get(string key) => (LanguageCode == "en" ? English : Russian).GetString(key, CultureInfo.InvariantCulture)
        ?? throw new MissingManifestResourceException($"Missing localization key: {key}");

    public static string Format(string template, params object?[] arguments) =>
        string.Format(CultureInfo.GetCultureInfo(LanguageCode), template, arguments);

    public static string Language => Get(nameof(Language));
    public static string InvalidLanguage => Get(nameof(InvalidLanguage));
    public static string Copy => Get(nameof(Copy));
    public static string SelectAll => Get(nameof(SelectAll));
    public static string AppTagline => Get(nameof(AppTagline));
    public static string DirectConnection => Get(nameof(DirectConnection));
    public static string YourComputer => Get(nameof(YourComputer));
    public static string Settings => Get(nameof(Settings));
    public static string DisplayName => Get(nameof(DisplayName));
    public static string ListeningPort => Get(nameof(ListeningPort));
    public static string ApplyChanges => Get(nameof(ApplyChanges));
    public static string ResetChanges => Get(nameof(ResetChanges));
    public static string StartWithWindows => Get(nameof(StartWithWindows));
    public static string StartWithWindowsHint => Get(nameof(StartWithWindowsHint));
    public static string YourAddress => Get(nameof(YourAddress));
    public static string NetworkComputers => Get(nameof(NetworkComputers));
    public static string RefreshPeers => Get(nameof(RefreshPeers));
    public static string NetworkComputersAccessibleName => Get(nameof(NetworkComputersAccessibleName));
    public static string Connect => Get(nameof(Connect));
    public static string CancelConnection => Get(nameof(CancelConnection));
    public static string ManualConnection => Get(nameof(ManualConnection));
    public static string PeerHost => Get(nameof(PeerHost));
    public static string PeerHostExample => Get(nameof(PeerHostExample));
    public static string Port => Get(nameof(Port));
    public static string PeerPort => Get(nameof(PeerPort));
    public static string ConnectByIp => Get(nameof(ConnectByIp));
    public static string ConnectionInstructions => Get(nameof(ConnectionInstructions));
    public static string Disconnect => Get(nameof(Disconnect));
    public static string DismissError => Get(nameof(DismissError));
    public static string EmptyConversationTitle => Get(nameof(EmptyConversationTitle));
    public static string EmptyConversationHint => Get(nameof(EmptyConversationHint));
    public static string Conversation => Get(nameof(Conversation));
    public static string ConversationMessageText => Get(nameof(ConversationMessageText));
    public static string FileTransferProgress => Get(nameof(FileTransferProgress));
    public static string SaveFile => Get(nameof(SaveFile));
    public static string RejectFile => Get(nameof(RejectFile));
    public static string CancelTransfer => Get(nameof(CancelTransfer));
    public static string AttachedFiles => Get(nameof(AttachedFiles));
    public static string RemoveAttachment => Get(nameof(RemoveAttachment));
    public static string AttachFilesHint => Get(nameof(AttachFilesHint));
    public static string AttachFiles => Get(nameof(AttachFiles));
    public static string MessageText => Get(nameof(MessageText));
    public static string Send => Get(nameof(Send));
    public static string MessageInputHint => Get(nameof(MessageInputHint));
    public static string DropFilesTitle => Get(nameof(DropFilesTitle));
    public static string DropFilesHint => Get(nameof(DropFilesHint));
    public static string Starting => Get(nameof(Starting));
    public static string ResolvingLocalAddress => Get(nameof(ResolvingLocalAddress));
    public static string ApplyingSettings => Get(nameof(ApplyingSettings));
    public static string DisconnectToApplySettings => Get(nameof(DisconnectToApplySettings));
    public static string UnappliedSettingsHint => Get(nameof(UnappliedSettingsHint));
    public static string SettingsApplied => Get(nameof(SettingsApplied));
    public static string StartingDiscovery => Get(nameof(StartingDiscovery));
    public static string ListenerNotStartedSettingsHint => Get(nameof(ListenerNotStartedSettingsHint));
    public static string SearchingPeers => Get(nameof(SearchingPeers));
    public static string NoPeersFound => Get(nameof(NoPeersFound));
    public static string NextConversation => Get(nameof(NextConversation));
    public static string SelectComputerHint => Get(nameof(SelectComputerHint));
    public static string Connected => Get(nameof(Connected));
    public static string NotConnected => Get(nameof(NotConnected));
    public static string ListenerStartFailed => Get(nameof(ListenerStartFailed));
    public static string ListenerNotStarted => Get(nameof(ListenerNotStarted));
    public static string WaitingForConnection => Get(nameof(WaitingForConnection));
    public static string Connecting => Get(nameof(Connecting));
    public static string ConnectionCancelled => Get(nameof(ConnectionCancelled));
    public static string ConnectionEstablished => Get(nameof(ConnectionEstablished));
    public static string SelfAuthor => Get(nameof(SelfAuthor));
    public static string FoldersNotSupported => Get(nameof(FoldersNotSupported));
    public static string FileTooLarge => Get(nameof(FileTooLarge));
    public static string ConnectionClosed => Get(nameof(ConnectionClosed));
    public static string PeerDisconnectedStatus => Get(nameof(PeerDisconnectedStatus));
    public static string OperationCancelledOrTimedOut => Get(nameof(OperationCancelledOrTimedOut));
    public static string PortAlreadyInUse => Get(nameof(PortAlreadyInUse));
    public static string ConnectionRefused => Get(nameof(ConnectionRefused));
    public static string ConnectionFailed => Get(nameof(ConnectionFailed));
    public static string InvalidPort => Get(nameof(InvalidPort));
    public static string WaitingForFileAcceptance => Get(nameof(WaitingForFileAcceptance));
    public static string SaveIncomingFilePrompt => Get(nameof(SaveIncomingFilePrompt));
    public static string SendingFile => Get(nameof(SendingFile));
    public static string ReceivingFile => Get(nameof(ReceivingFile));
    public static string FileSent => Get(nameof(FileSent));
    public static string FileSaved => Get(nameof(FileSaved));
    public static string FileRejected => Get(nameof(FileRejected));
    public static string TransferCancelled => Get(nameof(TransferCancelled));
    public static string ApplicationPathUnavailable => Get(nameof(ApplicationPathUnavailable));
    public static string StartupSettingsUnavailable => Get(nameof(StartupSettingsUnavailable));
    public static string OpenApplication => Get(nameof(OpenApplication));
    public static string Exit => Get(nameof(Exit));
    public static string TrayTooltip => Get(nameof(TrayTooltip));
    public static string EmptySettingsFile => Get(nameof(EmptySettingsFile));
    public static string SaveReceivedFile => Get(nameof(SaveReceivedFile));
    public static string AllFilesFilter => Get(nameof(AllFilesFilter));
    public static string WebLinksOnly => Get(nameof(WebLinksOnly));
    public static string NewMessage => Get(nameof(NewMessage));
    public static string InvalidDisplayName => Get(nameof(InvalidDisplayName));
    public static string BrowserOpenFailed => Get(nameof(BrowserOpenFailed));
    public static string InvalidPeerName => Get(nameof(InvalidPeerName));
    public static string DiscoveryAlreadyStarted => Get(nameof(DiscoveryAlreadyStarted));
    public static string DiscoveryNotStarted => Get(nameof(DiscoveryNotStarted));
    public static string MessageTooLarge => Get(nameof(MessageTooLarge));
    public static string InvalidFrameSize => Get(nameof(InvalidFrameSize));
    public static string EmptyNetworkMessage => Get(nameof(EmptyNetworkMessage));
    public static string InvalidFileMessage => Get(nameof(InvalidFileMessage));
    public static string InvalidFileName => Get(nameof(InvalidFileName));
    public static string PeerNotConnected => Get(nameof(PeerNotConnected));
    public static string PeerFileSupportRequired => Get(nameof(PeerFileSupportRequired));
    public static string TooManyPendingFiles => Get(nameof(TooManyPendingFiles));
    public static string AttachedFileChanged => Get(nameof(AttachedFileChanged));
    public static string SourceFileChanged => Get(nameof(SourceFileChanged));
    public static string FileOfferUnavailable => Get(nameof(FileOfferUnavailable));
    public static string FileOfferAlreadyHandled => Get(nameof(FileOfferAlreadyHandled));
    public static string DestinationAlreadyExists => Get(nameof(DestinationAlreadyExists));
    public static string InvalidTransferId => Get(nameof(InvalidTransferId));
    public static string InvalidFileOffer => Get(nameof(InvalidFileOffer));
    public static string DuplicateTransferId => Get(nameof(DuplicateTransferId));
    public static string InvalidFileChunk => Get(nameof(InvalidFileChunk));
    public static string FileConsentRequired => Get(nameof(FileConsentRequired));
    public static string FileExceedsDeclaredSize => Get(nameof(FileExceedsDeclaredSize));
    public static string IncompleteFile => Get(nameof(IncompleteFile));
    public static string PeerDisconnected => Get(nameof(PeerDisconnected));
    public static string ListenerAlreadyStarted => Get(nameof(ListenerAlreadyStarted));
    public static string InvalidPeerAddress => Get(nameof(InvalidPeerAddress));
    public static string ConnectionAlreadyActive => Get(nameof(ConnectionAlreadyActive));
    public static string IncompatiblePeer => Get(nameof(IncompatiblePeer));
    public static string InvalidMessage => Get(nameof(InvalidMessage));
    public static string StartupChangeFailed => Get(nameof(StartupChangeFailed));
    public static string StartupReadFailed => Get(nameof(StartupReadFailed));
    public static string SettingsReadFailed => Get(nameof(SettingsReadFailed));
    public static string SettingsApplyAndRestoreFailed => Get(nameof(SettingsApplyAndRestoreFailed));
    public static string SettingsRestored => Get(nameof(SettingsRestored));
    public static string SettingsSaveFailed => Get(nameof(SettingsSaveFailed));
    public static string PeersFound => Get(nameof(PeersFound));
    public static string LocalPort => Get(nameof(LocalPort));
    public static string LocalAddressNotFound => Get(nameof(LocalAddressNotFound));
    public static string LocalAddressAndPort => Get(nameof(LocalAddressAndPort));
    public static string LocalAddressLookupFailed => Get(nameof(LocalAddressLookupFailed));
    public static string DiscoveryUnavailable => Get(nameof(DiscoveryUnavailable));
    public static string ConcurrentFilesLimit => Get(nameof(ConcurrentFilesLimit));
    public static string AttachmentBatchLimit => Get(nameof(AttachmentBatchLimit));
    public static string DraftFilesLimit => Get(nameof(DraftFilesLimit));
    public static string TransferStatus => Get(nameof(TransferStatus));
    public static string TransferFailed => Get(nameof(TransferFailed));
    public static string SizeBytes => Get(nameof(SizeBytes));
    public static string SizeKilobytes => Get(nameof(SizeKilobytes));
    public static string SizeMegabytes => Get(nameof(SizeMegabytes));
    public static string SizeGigabytes => Get(nameof(SizeGigabytes));
    public static string FileNotificationWithCaption => Get(nameof(FileNotificationWithCaption));
    public static string FileNotification => Get(nameof(FileNotification));
    public static string DiscoveryStopped => Get(nameof(DiscoveryStopped));
    public static string DiscoveryRefreshFailed => Get(nameof(DiscoveryRefreshFailed));
    public static string InvalidMessageLength => Get(nameof(InvalidMessageLength));
    public static string ListenerFailed => Get(nameof(ListenerFailed));
    public static string AcceptConnectionFailed => Get(nameof(AcceptConnectionFailed));
    public static string ConnectionInterrupted => Get(nameof(ConnectionInterrupted));
    public static string DraftCounter => Get(nameof(DraftCounter));
    public static string AppName => Get(nameof(AppName));
    public static string MessageTimeFormat => Get(nameof(MessageTimeFormat));
    public static string PeerDescription => Get(nameof(PeerDescription));
    public static string ActivationListenerAlreadyRegistered => Get(nameof(ActivationListenerAlreadyRegistered));
}

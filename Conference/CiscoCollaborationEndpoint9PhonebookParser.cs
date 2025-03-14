using System.Text.RegularExpressions;
using AVCoders.Core;
using static System.Int32;

namespace AVCoders.Conference;

public record CiscoRoomOsPhonebookFolder(
    string Name,
    string FolderId,
    string LocalId,
    List<PhonebookBase> Items,
    PhonebookRequestStatus ContentDownloadState = PhonebookRequestStatus.NotBegun)
    : PhonebookFolder(Name, Items, ContentDownloadState);

public record CiscoRoomOsPhonebookContactMethod(string ContactMethodId, string Number, string Protocol)
    : PhonebookNumber(Number);

public record CiscoRoomOsPhonebookContact(string Name, string ContactId, List<PhonebookNumber> ContactMethods)
    : PhonebookContact(Name, ContactMethods);

public class CiscoCollaborationEndpoint9PhonebookParser : PhonebookParserBase
{
    private static readonly CiscoRoomOsPhonebookFolder RootFolder =
        new CiscoRoomOsPhonebookFolder("Top Level", String.Empty, String.Empty, new List<PhonebookBase>());
    private readonly string _phonebookType;
    private readonly int _waitTime;
    private readonly CommunicationClient _client;

    // Phonebook parsing variables
    private CancellationTokenSource _cancellationTokenSource = new ();
    private CiscoRoomOsPhonebookFolder _currentInjestfolder;
    private List<string> _searchResultsGather = new ();

    public CiscoCollaborationEndpoint9PhonebookParser(CommunicationClient client, string phonebookType = "Corporate", int waitTime = 5)
    : base(RootFolder)
    {
        _phonebookType = phonebookType;
        _waitTime = waitTime;
        _client = client;
        _client.ResponseHandlers += HandleResponse;
        _client.ConnectionStateHandlers += HandleConnectionState;
        _currentInjestfolder = (CiscoRoomOsPhonebookFolder)PhoneBook;
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
        _cancellationTokenSource = new CancellationTokenSource();
        
        if (connectionState == ConnectionState.Connected)
        {
            new Task(() =>
            {
                Task.Delay(TimeSpan.FromSeconds(_waitTime), _cancellationTokenSource.Token)
                    .Wait(_cancellationTokenSource.Token);
                RequestPhonebook();
            }).Start();
        }
    }

    ~CiscoCollaborationEndpoint9PhonebookParser()
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
    }

    private void HandleResponse(string response)
    {
        if (response.Contains("*r PhonebookSearchResult"))
            _searchResultsGather.Add(response);
        else if (response.Contains("** end"))
        {
            ProcessResponses();
            RequestNextPhoneBookFolder();
            _searchResultsGather.Clear();
        }
    }

    private void ProcessResponses()
    {
        List<CiscoRoomOsPhonebookFolder> folders = new();
        List<CiscoRoomOsPhonebookContact> contacts = new();
        var commandOutput = string.Join("", _searchResultsGather);
        var folderPattern = new Regex(@"\*r PhonebookSearchResult Folder \d+ LocalId: \""(?<LocalId>[^""]+)\""\n\*r PhonebookSearchResult Folder \d+ FolderId: \""(?<FolderId>[^""]+)\""\n\*r PhonebookSearchResult Folder \d+ Name: \""(?<Name>[^""]+)\""\n", RegexOptions.Multiline);
        var contactPattern = new Regex(@"\*r PhonebookSearchResult Contact (?<Index>\d+) Name: ""(?<Name>[^""]+)""\n\*r PhonebookSearchResult Contact \d+ ContactId: ""(?<ContactId>[^""]+)""\n\*r PhonebookSearchResult Contact \d+ Type: ""(?<Type>[^""]+)""\n\*r PhonebookSearchResult Contact \d+ ContactMethod \d+ ContactMethodId: ""(?<ContactMethodId>[^""]+)""\n\*r PhonebookSearchResult Contact \d+ ContactMethod \d+ Number: ""(?<Number>[^""]+)""\n\*r PhonebookSearchResult Contact \d+ ContactMethod \d+ Protocol: (?<Protocol>[^\n]+)\n\*r PhonebookSearchResult Contact \d+ ContactMethod \d+ CallRate: (?<CallRate>\d+)", RegexOptions.Multiline);
        var contactMethodPattern = new Regex(@"\*r PhonebookSearchResult Contact (?<Index>\d+) ContactMethod \d+ ContactMethodId: ""(?<ContactMethodId>[^""]+)""\n\*r PhonebookSearchResult Contact \d+ ContactMethod \d+ Number: ""(?<Number>[^""]+)""\n\*r PhonebookSearchResult Contact \d+ ContactMethod \d+ Protocol: (?<Protocol>[^\n]+)\n\*r PhonebookSearchResult Contact \d+ ContactMethod \d+ CallRate: (?<CallRate>\d+)", RegexOptions.Multiline);

        var folderMatches = folderPattern.Matches(commandOutput);

        foreach (Match folderInfo in folderMatches)
        {
            folders.Add(new CiscoRoomOsPhonebookFolder(
                folderInfo.Groups["Name"].Value,
                folderInfo.Groups["FolderId"].Value,
                folderInfo.Groups["LocalId"].Value,
                new List<PhonebookBase>()
                ));
        }
        _currentInjestfolder.Items.AddRange(folders);
        
        var contactMatches = contactPattern.Matches(commandOutput);
        var contactMethodMatches = contactMethodPattern.Matches(commandOutput);

        foreach (Match contactMatch in contactMatches)
        {
            var contactIndex = contactMatch.Groups["Index"].Value;
            var contact = new CiscoRoomOsPhonebookContact(
                contactMatch.Groups["Name"].Value,
                contactMatch.Groups["ContactId"].Value,
                new List<PhonebookNumber>()
            )
            {
                Name = contactMatch.Groups["Name"].Value,
            };
            
            foreach (Match methodMatch in contactMethodMatches)
            {
                if (methodMatch.Groups["Index"].Value == contactIndex)
                {
                    contact.ContactMethods.Add(new CiscoRoomOsPhonebookContactMethod(
                        methodMatch.Groups["ContactMethodId"].Value,
                        methodMatch.Groups["Number"].Value,
                        methodMatch.Groups["Protocol"].Value
                    ));
                }
            }
            contacts.Add(contact);
        }

        _currentInjestfolder.Items.AddRange(contacts);
        _currentInjestfolder.ContentDownloadState = PhonebookRequestStatus.Complete;
    }

    protected override void DoRequestPhonebook()
    {
        PhoneBook.Items.Clear();
        PhoneBook.ContentDownloadState = PhonebookRequestStatus.Downloading;
        _client.Send($"xCommand Phonebook Search PhonebookType: {_phonebookType} Offset:0\n");
    }

    private void RequestNextPhoneBookFolder()
    {
        CiscoRoomOsPhonebookFolder? unFetchedFolder = FindUnFetchedFolder(PhoneBook.Items);
        if (unFetchedFolder == null)
        {
            Log("Phonebook search complete");
            PhonebookRequestStatus = PhonebookRequestStatus.Complete;
            PhonebookUpdated?.Invoke(PhoneBook);
            return;
        }

        _currentInjestfolder = unFetchedFolder;

        _client.Send(
            $"xCommand Phonebook Search PhonebookType: {_phonebookType} Offset:0 FolderId: {_currentInjestfolder.FolderId}\n");
    }

    private CiscoRoomOsPhonebookFolder? FindUnFetchedFolder(List<PhonebookBase> phoneBookItems)
    {
        foreach (PhonebookBase item in phoneBookItems)
        {
            if (item.GetType() != typeof(CiscoRoomOsPhonebookFolder)) 
                continue;
            
            CiscoRoomOsPhonebookFolder folder = (CiscoRoomOsPhonebookFolder)item;
            if (folder.ContentDownloadState != PhonebookRequestStatus.Complete)
                return folder;
            if (FindUnFetchedFolder(folder.Items) != null)
                return FindUnFetchedFolder(folder.Items);
        }

        return null;
    }
}
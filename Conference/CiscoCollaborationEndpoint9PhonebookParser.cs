using AVCoders.Core;
using static System.Int32;

namespace AVCoders.Conference;

public record CiscoRoomOsPhonebookFolder(
    string Name,
    string FolderId,
    string LocalId,
    List<PhonebookBase> Items,
    bool ContentsFetched = false)
    : PhonebookFolder(Name, Items, ContentsFetched);

public record CiscoRoomOsPhonebookContactMethod(string ContactMethodId, string Number, string Protocol)
    : PhonebookNumber(Number);

public record CiscoRoomOsPhonebookContact(string Name, string ContactId, List<PhonebookNumber> ContactMethods)
    : PhonebookContact(Name, ContactMethods);

public class CiscoCollaborationEndpoint9PhonebookParser : PhonebookParserBase
{
    private readonly string _phonebookType;
    private readonly int _waitTime;
    private readonly CommunicationClient _client;

    // Phonebook parsing variables
    private readonly Dictionary<string, string> _injestFolder;
    private readonly Dictionary<string, string> _injestContact;
    private readonly List<Dictionary<string, string>> _injestContactMethods;
    private CancellationTokenSource _cancellationTokenSource = new ();
    private int _currentRow;
    private int _currentSubRow;
    private int _resultOffset;
    private int _resultTotalRows;
    private CiscoRoomOsPhonebookFolder? _currentInjestfolder;
    private int _currentLimit = 50;

    public CiscoCollaborationEndpoint9PhonebookParser(CommunicationClient client, string phonebookType = "Corporate", int waitTime = 5)
    : base(new CiscoRoomOsPhonebookFolder("Top Level", String.Empty, String.Empty, new List<PhonebookBase>()))
    {
        _phonebookType = phonebookType;
        _waitTime = waitTime;
        _client = client;
        _client.ResponseHandlers += HandleResponse;
        _client.ConnectionStateHandlers += HandleConnectionState;
        
        _injestFolder = new Dictionary<string, string>();
        _injestContact = new Dictionary<string, string>();
        _injestContactMethods = [new Dictionary<string, string>()];
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
        if (response.Contains("PhonebookSearchResult"))
            HandlePhonebookSearchResponse(response);
    }

    protected override void DoRequestPhonebook()
    {
        _client.Send($"xCommand Phonebook Search PhonebookType: {_phonebookType} Offset:0\n");
        LogHandlers?.Invoke($"sending xCommand Phonebook Search PhonebookType: {_phonebookType} Offset:0");
    }

    private void HandlePhonebookSearchResponse(string response)
    {
        if (response.Contains("status=OK"))
            CommunicationState = CommunicationState.Error;

        var responses = response.Split(' ');

        switch (responses[2])
        {
            case "ResultInfo":
            {
                switch (responses[3])
                {
                    case "Offset:":
                        _resultOffset = Parse(responses[4]);
                        Log($"The offset is {_resultOffset}");
                        CommunicationState = CommunicationState.Okay;
                        break;
                    case "TotalRows:":
                        _resultTotalRows = Parse(responses[4]);
                        Log($"Total rows is {_resultTotalRows}");
                        _currentRow = 1;
                        _currentSubRow = 1;
                        CommunicationState = CommunicationState.Okay;
                        break;
                    case "Limit:":
                        _currentLimit = Parse(responses[4]);
                        CommunicationState = CommunicationState.Okay;
                        break;
                    default:
                        Log($"Unhandled ResultInfo key: {responses[3]}");
                        CommunicationState = CommunicationState.Error;
                        break;
                }
                break;
            }
            case "Folder":
            {
                var loadResult = HandlePhonebookFolderResponse(response, responses);

                if (loadResult.state == EntryLoadState.Loaded && loadResult.responseRow == _resultTotalRows)
                {
                    if (_currentInjestfolder != null)
                        _currentInjestfolder.ContentsFetched = true;
                    RequestNextPhoneBookFolder();
                }

                CommunicationState = loadResult.state == EntryLoadState.Error ? CommunicationState.Error : CommunicationState.Okay;
                break;
            }
            case "Contact":
            {
                var loadResult = HandlePhonebookContactResponse(response, responses);

                int resultRow = loadResult.responseRow + _resultOffset;
                
                if (loadResult.state != EntryLoadState.Loaded)
                    CommunicationState = CommunicationState.Error;

                if (resultRow == _resultTotalRows)
                {
                    _currentInjestfolder!.ContentsFetched = true;
                    RequestNextPhoneBookFolder();
                    CommunicationState = CommunicationState.Okay;
                }

                if (loadResult.responseRow == _currentLimit)
                {
                    if (_resultTotalRows == _currentLimit)
                    {
                        _currentInjestfolder!.ContentsFetched = true;
                        RequestNextPhoneBookFolder();
                        CommunicationState = CommunicationState.Okay;
                    }
                    
                    _client.Send(
                        $"xCommand Phonebook Search PhonebookType: {_phonebookType} Offset:{_resultOffset + _currentLimit} FolderId: {_currentInjestfolder!.FolderId}\n");
                    
                    LogHandlers?.Invoke($" sending xCommand Phonebook Search PhonebookType: {_phonebookType} Offset:{_resultOffset + _currentLimit} FolderId: {_currentInjestfolder.FolderId}");
                    CommunicationState = CommunicationState.Okay;
                }
                break;
            }
        }

        Error($"Unhandled response key: {responses[2]}");
        CommunicationState = CommunicationState.Error;
    }

    private void RequestNextPhoneBookFolder()
    {
        if (_currentInjestfolder == null)
            PhoneBook.ContentsFetched = true;
        CiscoRoomOsPhonebookFolder? unFetchedFolder = FindUnFetchedFolder(PhoneBook.Items);
        if (unFetchedFolder == null)
        {
            Log("Phonebook search complete");
            PhonebookUpdated?.Invoke(PhoneBook);
            return;
        }

        _currentInjestfolder = unFetchedFolder;
        _currentRow = 0;

        _client.Send(
            $"xCommand Phonebook Search PhonebookType: {_phonebookType} Offset:0 FolderId: {_currentInjestfolder.FolderId}\n");
        
        LogHandlers?.Invoke($"sending xCommand Phonebook Search PhonebookType: {_phonebookType} Offset:0 FolderId: {_currentInjestfolder.FolderId}");
    }

    private CiscoRoomOsPhonebookFolder? FindUnFetchedFolder(List<PhonebookBase> phoneBookItems)
    {
        foreach (PhonebookBase item in phoneBookItems)
        {
            if (item.GetType() == typeof(CiscoRoomOsPhonebookFolder))
            {
                CiscoRoomOsPhonebookFolder folder = (CiscoRoomOsPhonebookFolder)item;
                if (!folder.ContentsFetched)
                    return folder;
                if (FindUnFetchedFolder(folder.Items) != null)
                    return FindUnFetchedFolder(folder.Items);
            }
        }

        return null;
    }

    private (int responseRow, EntryLoadState state) HandlePhonebookFolderResponse(string response, string[] responses)
    {
        var responseRow = Parse(responses[3]);
        if (responseRow != _currentRow)
        {
            Log($"Ignoring response as it's an invalid row, i'm expecting {_currentRow}");
            return (responseRow, EntryLoadState.Error);
        }

        if (responses[4] == "Name:")
        {
            var startIndex = response.IndexOf(':') + 1;
            var count = response.Length - startIndex;
            _injestFolder.Add("Name:", response.Substring(startIndex, count).Trim());
        }
        else
            _injestFolder.Add(responses[4], responses[5].Trim());

        if (!_injestFolder.ContainsKey("Name:") ||
            !_injestFolder.ContainsKey("FolderId:") ||
            !_injestFolder.ContainsKey("LocalId:"))
            return (responseRow, EntryLoadState.NotLoaded);

        _currentRow++;

        AddContactToFolder(new CiscoRoomOsPhonebookFolder(
            _injestFolder["Name:"].Trim('"'),
            _injestFolder["FolderId:"].Trim('"'),
            _injestFolder["LocalId:"].Trim('"'),
            new List<PhonebookBase>()));

        _injestFolder.Clear();
        return (responseRow, EntryLoadState.Loaded);
    }

    private (int responseRow, EntryLoadState state) HandlePhonebookContactResponse(string response, string[] responses)
    {
        var responseRow = Parse(responses[3]);
        if (responseRow != _currentRow)
        {
            Log($"Ignoring response as it's an invalid row, i'm expecting {_currentRow}");
            return (responseRow, EntryLoadState.Error);
        }

        switch (responses[4])
        {
            case "Name:":
                var startIndex = response.IndexOf(':') + 1;
                var count = response.Length - startIndex;
                _injestContact.Add("Name:", response.Substring(startIndex, count).Trim().Trim('"'));
                break;
            case "ContactMethod":
                var methodId = Parse(responses[5]);
                if (methodId > _currentSubRow)
                {
                    _injestContactMethods.Add(new Dictionary<string, string>());
                    _currentSubRow = methodId;
                }
                
                _injestContactMethods[methodId - 1].Add(responses[6], responses[7].Trim().Trim('"'));
                break;
            default:
                _injestContact.Add(responses[4], responses[5].Trim().Trim('"'));
                break;
        }

        if (!_injestContact.ContainsKey("Name:") ||
            !_injestContact.ContainsKey("ContactId:") ||
            !AllContactMethodsArePopulated())
            return (responseRow, EntryLoadState.NotLoaded);

        List<PhonebookNumber> contactMethods = new List<PhonebookNumber>();

        _injestContactMethods.ForEach(contactMethod =>
        {
            contactMethods.Add(new CiscoRoomOsPhonebookContactMethod(
                contactMethod["ContactMethodId:"],
                contactMethod["Number:"],
                contactMethod["Protocol:"]));
        });

        AddContactToFolder(new CiscoRoomOsPhonebookContact(_injestContact["Name:"], _injestContact["ContactId:"],
            contactMethods));

        _injestContact.Clear();
        _injestContactMethods.Clear();
        _injestContactMethods.Add(new Dictionary<string, string>());
        _currentSubRow = 1;
        _currentRow++;
        // _currentSubRow = 1;
        return (responseRow, EntryLoadState.Loaded);
    }

    private void AddContactToFolder(PhonebookBase contact)
    {
        if (_currentInjestfolder == null)
        {
            PhoneBook.Items.Add(contact);
            return;
        }

        PhoneBook.Items.ForEach(item =>
        {
            if (item.GetType() == typeof(CiscoRoomOsPhonebookFolder))
            {
                if (item == _currentInjestfolder)
                {
                    CiscoRoomOsPhonebookFolder folder = (CiscoRoomOsPhonebookFolder)item;
                    folder.Items.Add(contact);
                }
            }
        });
    }

    private bool AllContactMethodsArePopulated()
    {
        if(_injestContactMethods.Count == 0)
            return false;
        foreach (var dictionaryEntry in _injestContactMethods)
        {
            if (!dictionaryEntry.ContainsKey("ContactMethodId:"))
                return false;
            if (!dictionaryEntry.ContainsKey("Number:"))
                return false;
            if (!dictionaryEntry.ContainsKey("Protocol:"))
                return false;
        }

        return true;
    }
}
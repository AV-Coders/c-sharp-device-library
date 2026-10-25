using AVCoders.Core;

namespace AVCoders.Conference;

public record CiscoRoomOsPhonebookFolder(
    string Name,
    string FolderId,
    string LocalId,
    List<PhonebookBase> Items)
    : PhonebookFolder(Name, FolderId, Items);

public record CiscoRoomOsPhonebookContactMethod(string ContactMethodId, string Number, string Protocol)
    : PhonebookNumber(Number);

public record CiscoRoomOsPhonebookContact(string Name, string ContactId, List<PhonebookNumber> ContactMethods)
    : PhonebookContact(Name, ContactMethods);

// Sealed so the constructor's RequestPhonebook call cannot dispatch to a subclass override
// running before that subclass's constructor (S1699).
public sealed class CiscoRoomOsPhonebookParser : PhonebookParserBase
{
    private const int SearchLimit = 300;
    private const int MinimumEntryTokens = 5;
    private const int MethodIdIndex = 5;
    private static readonly char[] LineSeparators = ['\r', '\n'];
    private readonly string _phonebookType;
    private readonly object _searchLock = new();

    private record PhonebookSearch(string Command, string? TriggerId);

    private record PhonebookRequest(string? TriggerId);

    // Phonebook parsing variables
    private readonly Dictionary<int, Dictionary<string, string>> _injestFolders = new();
    private readonly Dictionary<int, Dictionary<string, string>> _injestContacts = new();
    private readonly Dictionary<int, Dictionary<int, Dictionary<string, string>>> _injestContactMethods = new();
    private readonly HashSet<int> _loadedRows = [];
    private int _resultOffset;
    private int _resultTotalRows;
    private CiscoRoomOsPhonebookFolder? _currentInjestfolder = null;
    private int _currentLimit = SearchLimit;
    private bool _inPhonebookSearchResult;
    private bool _inPhonebookSearchError;
    private PhonebookSearch? _outstandingSearch;
    private Timer? _searchTimer;
    private PhonebookRequest? _heldRequest;

    public TimeSpan SearchTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public CiscoRoomOsPhonebookParser(CommunicationClient communicationClient, string phonebookType = "Corporate") :
        base(phonebookType, communicationClient)
    {
        _phonebookType = phonebookType;
        PhoneBook = new CiscoRoomOsPhonebookFolder("Top Level", string.Empty, string.Empty, []);

        communicationClient.ResponseHandlers += HandleResponse;
        communicationClient.ConnectionStateHandlers += HandleConnectionState;
        if (communicationClient.ConnectionState == ConnectionState.Connected)
            RequestPhonebook();
    }

    public override void RequestPhonebook()
    {
        PhonebookSearch search;
        lock (_searchLock)
        {
            if (_outstandingSearch != null)
            {
                _heldRequest = new PhonebookRequest(Trigger.Id);
                using (PushProperties("RequestPhonebook"))
                    LogDebug("Holding the phonebook request until {Search} ends", _outstandingSearch.Command.TrimEnd());
                return;
            }

            search = RecordSearch(RootSearch(), Trigger.Id);
        }

        StartPhonebookLoad(search);
    }

    private string RootSearch() => $"xCommand Phonebook Search PhonebookType: {_phonebookType} Offset:0 Limit: {SearchLimit}\n";

    private PhonebookSearch RecordSearch(string command, string? triggerId)
    {
        _searchTimer?.Dispose();
        var search = new PhonebookSearch(command, triggerId);
        _outstandingSearch = search;
        using (ExecutionContext.SuppressFlow())
            _searchTimer = new Timer(HandleSearchTimeout, search, SearchTimeout, Timeout.InfiniteTimeSpan);
        return search;
    }

    private PhonebookSearch? ReleaseHeldRequest()
    {
        if (_heldRequest == null)
            return null;
        var search = RecordSearch(RootSearch(), _heldRequest.TriggerId);
        _heldRequest = null;
        return search;
    }

    private void EndOutstandingSearch()
    {
        _searchTimer?.Dispose();
        _outstandingSearch = null;
    }

    private void StartPhonebookLoad(PhonebookSearch search)
    {
        using (Trigger.Resume(search.TriggerId))
        {
            _currentInjestfolder = null;
            _inPhonebookSearchResult = false;
            _inPhonebookSearchError = false;
            PhoneBook = new CiscoRoomOsPhonebookFolder("Top Level", string.Empty, string.Empty, []);
            CommunicationClient.Send(search.Command);
            AddEvent(EventType.DriverState, "Requesting Phonebook");
        }
    }

    private void SendNextSearch(string command)
    {
        PhonebookSearch? heldSearch;
        lock (_searchLock)
        {
            heldSearch = ReleaseHeldRequest();
            if (heldSearch == null)
                RecordSearch(command, Trigger.Id);
        }

        if (heldSearch != null)
        {
            StartPhonebookLoad(heldSearch);
            return;
        }

        CommunicationClient.Send(command);
    }

    private void CompleteLoad()
    {
        PhonebookFolder phonebook;
        PhonebookSearch? heldSearch;
        lock (_searchLock)
        {
            EndOutstandingSearch();
            heldSearch = ReleaseHeldRequest();
            phonebook = PhoneBook;
        }

        AddEvent(EventType.DriverState, "Phonebook search complete");
        LogDebug("Phonebook search complete");
        PhonebookUpdated?.Invoke(phonebook);
        if (heldSearch != null)
            StartPhonebookLoad(heldSearch);
    }

    private void EndFailedSearch()
    {
        PhonebookSearch? heldSearch;
        lock (_searchLock)
        {
            EndOutstandingSearch();
            heldSearch = ReleaseHeldRequest();
        }

        if (heldSearch != null)
            StartPhonebookLoad(heldSearch);
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState != ConnectionState.Disconnected)
            return;
        lock (_searchLock)
        {
            EndOutstandingSearch();
            _heldRequest = null;
        }
    }

    private void HandleSearchTimeout(object? state)
    {
        try
        {
            var search = (PhonebookSearch)state!;
            PhonebookSearch? heldSearch;
            lock (_searchLock)
            {
                if (!ReferenceEquals(search, _outstandingSearch))
                    return;
                EndOutstandingSearch();
                heldSearch = ReleaseHeldRequest();
            }

            using (Trigger.Resume(search.TriggerId))
            using (PushProperties("HandleSearchTimeout"))
                LogWarning("No complete reply to {Search} within {Timeout} s", search.Command.TrimEnd(), SearchTimeout.TotalSeconds);
            if (heldSearch != null)
                StartPhonebookLoad(heldSearch);
        }
        catch (Exception e)
        {
            LogException(e, "Sending the held phonebook request after the search timeout failed");
        }
    }

    private void HandleResponse(string response)
    {
        foreach (var line in response.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
            ProcessResponse(line);
    }

    private void ProcessResponse(string response)
    {
        // A new CLI session; request the phonebook once the codec declares itself ready.
        if (response.StartsWith("*r Login successful"))
        {
            RequestPhonebook();
            return;
        }

        using (PushProperties("HandlePhonebookSearchResponse"))
        {
            if (response.StartsWith("** end"))
            {
                if (_inPhonebookSearchError)
                {
                    _inPhonebookSearchError = false;
                    EndFailedSearch();
                    return;
                }
                if (!_inPhonebookSearchResult)
                    return;
                _inPhonebookSearchResult = false;
                PhonebookSearch? heldSearch;
                lock (_searchLock)
                    heldSearch = ReleaseHeldRequest();
                if (heldSearch != null)
                {
                    StartPhonebookLoad(heldSearch);
                    return;
                }
                LogDebug("Phonebook search chunk ended. Total processed so far: {Count}", _loadedRows.Count);
                ProcessInjestData();
                CheckForCompletion();
                return;
            }

            if (!response.Contains("*r PhonebookSearchResult"))
                return;

            if (response.Contains("(status=OK):"))
            {
                _inPhonebookSearchResult = true;
                return;
            }

            if (response.Contains("(status="))
                _inPhonebookSearchError = true;

            if (_inPhonebookSearchError)
            {
                LogWarning("Phonebook search failed: {Response}", response);
                CommunicationState = CommunicationState.Error;
                return;
            }

            var responses = response.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (responses.Length < 3)
                return;

            switch (responses[2])
            {
                case "ResultInfo" when responses.Length >= MinimumEntryTokens:
                {
                    switch (responses[3])
                    {
                        case "Offset:":
                            _resultOffset = Int32.Parse(responses[4]);
                            LogVerbose("The offset is {ResultOffset}", _resultOffset);
                            CommunicationState = CommunicationState.Okay;
                            return;
                        case "TotalRows:":
                            _resultTotalRows = Int32.Parse(responses[4]);
                            LogVerbose("Total rows is {ResultTotalRows}", _resultTotalRows);
                            _injestFolders.Clear();
                            _injestContacts.Clear();
                            _injestContactMethods.Clear();
                            _loadedRows.Clear();
                            CommunicationState = CommunicationState.Okay;
                            return;
                        case "Limit:":
                            _currentLimit = Int32.Parse(responses[4]);
                            CommunicationState = CommunicationState.Okay;
                            return;
                        default:
                            LogInformation("Unhandled ResultInfo key: {Response}", responses[3]);
                            CommunicationState = CommunicationState.Error;
                            AddEvent(EventType.Error, $"Unhandled ResultInfo key: {responses[3]}");
                            return;
                    }
                }
                case "Folder" when responses.Length >= MinimumEntryTokens:
                {
                    var loadResult = HandlePhonebookFolderResponse(response, responses);

                    CommunicationState = loadResult.state == EntryLoadState.Error
                        ? CommunicationState.Error
                        : CommunicationState.Okay;
                    return;
                }
                case "Contact" when responses.Length >= MinimumEntryTokens:
                {
                    var loadResult = HandlePhonebookContactResponse(response, responses);

                    CommunicationState = loadResult.state == EntryLoadState.Error
                        ? CommunicationState.Error
                        : CommunicationState.Okay;
                    return;
                }
                default:
                    LogDebug("Unhandled response: {Response}", response);
                    CommunicationState = CommunicationState.Error;
                    break;
            }
        }
    }

    private void RequestNextPhoneBookFolder()
    {
        if (_currentInjestfolder == null)
            PhoneBook.ContentsFetched = true;

        CiscoRoomOsPhonebookFolder? unFetchedFolder = FindUnFetchedFolder(PhoneBook.Items);
        if (unFetchedFolder == null)
        {
            CompleteLoad();
            return;
        }

        _currentInjestfolder = unFetchedFolder;
        AddEvent(EventType.DriverState, $"Requesting contents for folder {_currentInjestfolder.Name} ({_currentInjestfolder.FolderId})");

        _injestFolders.Clear();
        _injestContacts.Clear();
        _injestContactMethods.Clear();
        _loadedRows.Clear();

        SendNextSearch(
            $"xCommand Phonebook Search PhonebookType: {_phonebookType} Offset:0 FolderId: {_currentInjestfolder.FolderId} Limit: {SearchLimit}\n");
    }

    private CiscoRoomOsPhonebookFolder? FindUnFetchedFolder(List<PhonebookBase> phoneBookItems)
    {
        foreach (PhonebookBase item in phoneBookItems)
        {
            if (item is CiscoRoomOsPhonebookFolder folder)
            {
                if (!folder.ContentsFetched)
                    return folder;

                var nestedUnfetched = FindUnFetchedFolder(folder.Items);
                if (nestedUnfetched != null)
                    return nestedUnfetched;
            }
        }

        return null;
    }

    private (int responseRow, EntryLoadState state) HandlePhonebookFolderResponse(string response, string[] responses)
    {
        var responseRow = Int32.Parse(responses[3]);
        if (!_injestFolders.ContainsKey(responseRow))
            _injestFolders[responseRow] = new Dictionary<string, string>();

        var folderData = _injestFolders[responseRow];

        if (responses[4] == "Name:")
        {
            var startIndex = response.IndexOf("Name:") + 5;
            folderData["Name:"] = response.Substring(startIndex).Trim().Trim('"');
        }
        else if (responses.Length > 5)
            folderData[responses[4]] = responses[5].Trim().Trim('"');

        return (responseRow, EntryLoadState.Loaded);
    }

    private (int responseRow, EntryLoadState state) HandlePhonebookContactResponse(string response, string[] responses)
    {
        var responseRow = Int32.Parse(responses[3]);
        if (!_injestContacts.ContainsKey(responseRow))
            _injestContacts[responseRow] = new Dictionary<string, string>();

        var contactData = _injestContacts[responseRow];

        switch (responses[4])
        {
            case "Name:":
                var nameStartIndex = response.IndexOf("Name:") + 5;
                contactData["Name:"] = response.Substring(nameStartIndex).Trim().Trim('"');
                break;
            case "ContactMethod" when responses.Length > MethodIdIndex:
                var methodId = Int32.Parse(responses[MethodIdIndex]);
                if (!_injestContactMethods.ContainsKey(responseRow))
                    _injestContactMethods[responseRow] = new Dictionary<int, Dictionary<string, string>>();

                var methods = _injestContactMethods[responseRow];
                if (!methods.ContainsKey(methodId))
                    methods[methodId] = new Dictionary<string, string>();

                if (responses.Length > 6)
                {
                    if (responses[6] == "Number:")
                    {
                        var numberStartIndex = response.IndexOf("Number:") + 7;
                        methods[methodId]["Number:"] = response.Substring(numberStartIndex).Trim().Trim('"');
                    }
                    else if (responses.Length > 7)
                    {
                        methods[methodId][responses[6]] = responses[7].Trim().Trim('"');
                    }
                }

                methods[methodId]["ContactMethodId:"] = methodId.ToString();
                break;
            default:
                if (responses.Length > 5)
                    contactData[responses[4]] = responses[5].Trim().Trim('"');
                break;
        }

        return (responseRow, EntryLoadState.Loaded);
    }

    private void CheckForCompletion()
    {
        ProcessInjestData();

        int maxRow = 0;
        if (_injestFolders.Count > 0) maxRow = Math.Max(maxRow, _injestFolders.Keys.Max());
        if (_injestContacts.Count > 0) maxRow = Math.Max(maxRow, _injestContacts.Keys.Max());

        int currentTotalProcessed = maxRow + _resultOffset;

        LogDebug(
            "CheckForCompletion: maxRow={MaxRow}, offset={Offset}, totalRows={TotalRows}, currentTotalProcessed={CurrentTotalProcessed}",
            maxRow, _resultOffset, _resultTotalRows, currentTotalProcessed);

        if (currentTotalProcessed >= _resultTotalRows || (maxRow > 0 && maxRow == _currentLimit))
        {
            if (currentTotalProcessed >= _resultTotalRows)
            {
                if (_currentInjestfolder != null)
                {
                    _currentInjestfolder.ContentsFetched = true;
                    LogDebug("Setting ContentsFetched for folder {FolderName} ({FolderId})", _currentInjestfolder.Name,
                        _currentInjestfolder.FolderId);
                }

                RequestNextPhoneBookFolder();
            }
            else if (maxRow >= _currentLimit)
            {
                var nextOffset = _resultOffset + _currentLimit;
                if (_currentInjestfolder == null)
                    SendNextSearch(
                        $"xCommand Phonebook Search PhonebookType: {_phonebookType} Offset:{nextOffset} Limit: {SearchLimit}\n");
                else
                    SendNextSearch(
                        $"xCommand Phonebook Search PhonebookType: {_phonebookType} Offset:{nextOffset} FolderId: {_currentInjestfolder.FolderId} Limit: {SearchLimit}\n");
            }
        }
    }

    private void ProcessInjestData()
    {
        var sortedRows = _injestFolders.Keys.Concat(_injestContacts.Keys).Distinct().OrderBy(r => r).ToList();

        foreach (var row in sortedRows)
        {
            if (_loadedRows.Contains(row)) continue;

            bool complete = false;
            if (_injestFolders.TryGetValue(row, out var folderData))
            {
                if (folderData.TryGetValue("Name:", out var name))
                {
                    AddFolder(new CiscoRoomOsPhonebookFolder(
                        name,
                        folderData.GetValueOrDefault("FolderId:", string.Empty),
                        folderData.GetValueOrDefault("LocalId:", string.Empty),
                        []));

                    complete = folderData.ContainsKey("FolderId:") && folderData.ContainsKey("LocalId:");
                }
            }
            else if (_injestContacts.TryGetValue(row, out var contactData))
            {
                if (contactData.ContainsKey("Name:") && contactData.ContainsKey("ContactId:"))
                {
                    List<PhonebookNumber> contactMethods = [];
                    if (_injestContactMethods.TryGetValue(row, out var methodDict))
                    {
                        var methodEntries =
                            methodDict.Values.OrderBy(m => int.Parse(m.GetValueOrDefault("ContactMethodId:", "0")));
                        foreach (var methodEntry in methodEntries)
                        {
                            contactMethods.Add(new CiscoRoomOsPhonebookContactMethod(
                                methodEntry.GetValueOrDefault("ContactMethodId:", "0"),
                                methodEntry.GetValueOrDefault("Number:", string.Empty),
                                methodEntry.GetValueOrDefault("Protocol:", string.Empty)));
                        }
                    }

                    AddContact(new CiscoRoomOsPhonebookContact(contactData["Name:"], contactData["ContactId:"],
                        contactMethods));
                    complete = true;
                }
            }

            if (complete)
                _loadedRows.Add(row);
        }
    }


    private CiscoRoomOsPhonebookFolder CurrentParentFolder()
    {
        if (_currentInjestfolder == null || _currentInjestfolder.FolderId == PhoneBook.FolderId)
            return (CiscoRoomOsPhonebookFolder)PhoneBook;
        return FindFolderById(PhoneBook.Items, _currentInjestfolder.FolderId) ?? (CiscoRoomOsPhonebookFolder)PhoneBook;
    }

    private void AddFolder(CiscoRoomOsPhonebookFolder folder)
    {
        var items = CurrentParentFolder().Items;

        int existingIndex = items.FindIndex(i => i is CiscoRoomOsPhonebookFolder existing && existing.FolderId == folder.FolderId);
        if (existingIndex < 0)
        {
            items.Add(folder);
            return;
        }

        var oldFolder = (CiscoRoomOsPhonebookFolder)items[existingIndex];
        var updated = new CiscoRoomOsPhonebookFolder(folder.Name, folder.FolderId, folder.LocalId, oldFolder.Items);
        updated.ContentsFetched = oldFolder.ContentsFetched || folder.ContentsFetched;
        items[existingIndex] = updated;

        if (ReferenceEquals(_currentInjestfolder, oldFolder))
            _currentInjestfolder = updated;
    }

    private void AddContact(CiscoRoomOsPhonebookContact contact)
    {
        var items = CurrentParentFolder().Items;

        int existingIndex = items.FindIndex(i => i is CiscoRoomOsPhonebookContact existing && existing.ContactId == contact.ContactId);
        if (existingIndex < 0)
        {
            items.Add(contact);
            return;
        }

        var oldContact = (CiscoRoomOsPhonebookContact)items[existingIndex];
        var mergedMethods = oldContact.ContactMethods.ToList();
        foreach (var newMethod in contact.ContactMethods)
        {
            var existingMethod = mergedMethods.Cast<CiscoRoomOsPhonebookContactMethod>()
                .FirstOrDefault(m =>
                    m.ContactMethodId ==
                    ((CiscoRoomOsPhonebookContactMethod)newMethod).ContactMethodId);
            if (existingMethod != null)
            {
                int idx = mergedMethods.IndexOf(existingMethod);
                mergedMethods[idx] = newMethod;
            }
            else
            {
                mergedMethods.Add(newMethod);
            }
        }

        items[existingIndex] = new CiscoRoomOsPhonebookContact(contact.Name, contact.ContactId, mergedMethods);
    }

    public PhonebookFolder? FindFolderById(string folderId)
    {
        if (PhoneBook.FolderId == folderId) return PhoneBook;
        return FindFolderById(PhoneBook.Items, folderId);
    }

    private CiscoRoomOsPhonebookFolder? FindFolderById(List<PhonebookBase> items, string folderId)
    {
        foreach (var item in items)
        {
            if (item is CiscoRoomOsPhonebookFolder folder)
            {
                if (folder.FolderId == folderId) return folder;
                var found = FindFolderById(folder.Items, folderId);
                if (found != null) return found;
            }
        }

        return null;
    }

    public override void PowerOn() => RequestPhonebook();

    public override void PowerOff() => RequestPhonebook();
}
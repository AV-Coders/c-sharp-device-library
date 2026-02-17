using AVCoders.Core;
using Serilog;

namespace AVCoders.Conference;

public record CiscoRoomOsPhonebookFolder(
    string Name,
    string FolderId,
    string LocalId,
    List<PhonebookBase> Items)
    : PhonebookFolder(Name, Items)
{
    public bool ContentsFetched { get; set; }
}

public record CiscoRoomOsPhonebookContactMethod(string ContactMethodId, string Number, string Protocol)
    : PhonebookNumber(Number);

public record CiscoRoomOsPhonebookContact(string Name, string ContactId, List<PhonebookNumber> ContactMethods)
    : PhonebookContact(Name, ContactMethods);

public class CiscoCE9PhonebookParser : PhonebookParserBase
{
    private readonly string _phonebookType;
    public readonly CiscoRoomOsPhonebookFolder PhoneBook;

    // Phonebook parsing variables
    private readonly Dictionary<int, Dictionary<string, string>> _injestFolders = new();
    private readonly Dictionary<int, Dictionary<string, string>> _injestContacts = new();
    private readonly Dictionary<int, Dictionary<int, Dictionary<string, string>>> _injestContactMethods = new();
    private readonly HashSet<int> _loadedRows = [];
    private int _resultOffset;
    private int _resultTotalRows;
    private CiscoRoomOsPhonebookFolder? _currentInjestfolder = null;
    private int _currentLimit = 50;

    public CiscoCE9PhonebookParser(CommunicationClient communicationClient, string phonebookType = "Corporate") : base(phonebookType, communicationClient)
    {
        _phonebookType = phonebookType;
        PhoneBook = new CiscoRoomOsPhonebookFolder("Top Level", string.Empty, string.Empty, []);

        communicationClient.ResponseHandlers += HandleResponse;
        communicationClient.ConnectionStateHandlers += HandleConnectionState;
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if(connectionState != ConnectionState.Connected)
            return;
        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            RequestPhonebook();
        });
    }

    public void RequestPhonebook()
    {
        CommunicationClient.Send($"xCommand Phonebook Search PhonebookType: {_phonebookType} Offset:0\n");
        AddEvent(EventType.Other, "Requesting Phonebook");
    }

    private void HandleResponse(string response)
    {
        using (PushProperties("HandlePhonebookSearchResponse"))
        {
            if (response.Contains("** end"))
            {
                ProcessInjestData();
                CheckForCompletion();
                return;
            }

            if( !response.Contains("*r PhonebookSearchResult"))
                   return;

            if (response.Contains("status=OK"))
            {
                CommunicationState = CommunicationState.Okay;
                return;
            }

            var responses = response.Split(' ');

            switch (responses[2])
            {
                case "ResultInfo":
                {
                    switch (responses[3])
                    {
                        case "Offset:":
                            _resultOffset = Int32.Parse(responses[4]);
                            Log.Verbose("The offset is {ResultOffset}", _resultOffset);
                            CommunicationState = CommunicationState.Okay;
                            return;
                        case "TotalRows:":
                            _resultTotalRows = Int32.Parse(responses[4]);
                            Log.Verbose("Total rows is {ResultTotalRows}", _resultTotalRows);
                            _injestFolders.Clear();
                            _injestContacts.Clear();
                            _injestContactMethods.Clear();
                            _loadedRows.Clear();
                            CommunicationState = CommunicationState.Okay;

                            if (_resultTotalRows == 0)
                            {
                                if (_currentInjestfolder != null)
                                    _currentInjestfolder.ContentsFetched = true;
                                RequestNextPhoneBookFolder();
                            }
                            return;
                        case "Limit:":
                            _currentLimit = Int32.Parse(responses[4]);
                            CommunicationState = CommunicationState.Okay;
                            return;
                        default:
                            Log.Information("Unhandled ResultInfo key: {Response}", responses[3]);
                            CommunicationState = CommunicationState.Error;
                            AddEvent(EventType.Error,  $"Unhandled ResultInfo key: {responses[3]}");
                            return;
                    }
                }
            case "Folder":
                {
                    var loadResult = HandlePhonebookFolderResponse(response, responses);

                    CommunicationState = loadResult.state == EntryLoadState.Error
                        ? CommunicationState.Error
                        : CommunicationState.Okay;
                    return;
                }
            case "Contact":
                {
                    var loadResult = HandlePhonebookContactResponse(response, responses);

                    CommunicationState = loadResult.state == EntryLoadState.Error
                        ? CommunicationState.Error
                        : CommunicationState.Okay;
                    return;
                }
            }

            Log.Debug("Unhandled response key: {Response}", responses[2]);
            CommunicationState = CommunicationState.Error;
        }
    }

    private void RequestNextPhoneBookFolder()
    {
        if (_currentInjestfolder == null)
            PhoneBook.ContentsFetched = true;
        CiscoRoomOsPhonebookFolder? unFetchedFolder = FindUnFetchedFolder(PhoneBook.Items);
        if (unFetchedFolder == null)
        {
            Log.Debug("Phonebook search complete");
            PhonebookUpdated?.Invoke(PhoneBook);
            return;
        }

        _currentInjestfolder = unFetchedFolder;

        _injestFolders.Clear();
        _injestContacts.Clear();
        _injestContactMethods.Clear();
        _loadedRows.Clear();

        CommunicationClient.Send($"xCommand Phonebook Search PhonebookType: {_phonebookType} Offset:0 FolderId: {_currentInjestfolder.FolderId}\n");
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
            case "ContactMethod":
                var methodId = Int32.Parse(responses[5]);
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

        if (currentTotalProcessed == _resultTotalRows || (maxRow > 0 && maxRow == _currentLimit))
        {
            if (currentTotalProcessed == _resultTotalRows)
            {
                if (_currentInjestfolder != null)
                    _currentInjestfolder.ContentsFetched = true;
                RequestNextPhoneBookFolder();
            }
            else if (maxRow == _currentLimit)
            {
                CommunicationClient.Send(
                    $"xCommand Phonebook Search PhonebookType: {_phonebookType} Offset:{_resultOffset + _currentLimit} FolderId: {_currentInjestfolder?.FolderId ?? string.Empty}\n");
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
                if (folderData.TryGetValue("Name:", out var data))
                {
                    AddContactToFolder(new CiscoRoomOsPhonebookFolder(
                        data,
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
                        var methodEntries = methodDict.Values.OrderBy(m => int.Parse(m.GetValueOrDefault("ContactMethodId:", "0")));
                        foreach (var methodEntry in methodEntries)
                        {
                            contactMethods.Add(new CiscoRoomOsPhonebookContactMethod(
                                methodEntry.GetValueOrDefault("ContactMethodId:", "0"),
                                methodEntry.GetValueOrDefault("Number:", string.Empty),
                                methodEntry.GetValueOrDefault("Protocol:", string.Empty)));
                        }
                    }

                    AddContactToFolder(new CiscoRoomOsPhonebookContact(contactData["Name:"], contactData["ContactId:"], contactMethods));
                    complete = AllContactMethodsArePopulated(row);
                }
            }

            if (complete)
                _loadedRows.Add(row);
        }
    }


    private void AddContactToFolder(PhonebookBase contact)
    {
        List<PhonebookBase> items;
        if (_currentInjestfolder == null)
        {
            items = PhoneBook.Items;
        }
        else
        {
            // Root folder itself or finding it by ID in the tree
            if (_currentInjestfolder.FolderId == PhoneBook.FolderId)
                items = PhoneBook.Items;
            else
                items = FindFolderById(PhoneBook.Items, _currentInjestfolder.FolderId)?.Items ?? PhoneBook.Items;
        }

        int existingIndex = items.FindIndex(i => i.Name == contact.Name);
        if (existingIndex >= 0)
        {
            if (items[existingIndex].GetType() == contact.GetType())
            {
                if (contact is CiscoRoomOsPhonebookFolder newFolder && items[existingIndex] is CiscoRoomOsPhonebookFolder oldFolder)
                {
                    // Update and preserve sub-items
                    var updated = new CiscoRoomOsPhonebookFolder(newFolder.Name, newFolder.FolderId, newFolder.LocalId, oldFolder.Items);
                    updated.ContentsFetched = oldFolder.ContentsFetched || newFolder.ContentsFetched;
                    items[existingIndex] = updated;
                    
                    // If this was the folder we are currently filling, update the reference
                    if (_currentInjestfolder == oldFolder)
                        _currentInjestfolder = updated;
                }
                else
                {
                    items[existingIndex] = contact;
                }
            }
        }
        else
        {
            items.Add(contact);
        }
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

    private bool AllContactMethodsArePopulated(int responseRow)
    {
        if (!_injestContactMethods.ContainsKey(responseRow))
            return false;

        foreach (var dictionaryEntry in _injestContactMethods[responseRow].Values)
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

    public override void PowerOn() => RequestPhonebook();

    public override void PowerOff() => RequestPhonebook();
}
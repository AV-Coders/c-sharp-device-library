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
    private readonly Dictionary<string, string> _injestFolder;
    private readonly Dictionary<string, string> _injestContact;
    private readonly List<Dictionary<string, string>> _injestContactMethods;
    private int _currentRow;
    private int _currentSubRow;
    private int _resultOffset;
    private int _resultTotalRows;
    private CiscoRoomOsPhonebookFolder? _currentInjestfolder = null;
    private int _currentLimit = 50;

    public CiscoCE9PhonebookParser(CommunicationClient communicationClient, string phonebookType = "Corporate") : base(phonebookType, communicationClient)
    {
        _phonebookType = phonebookType;
        PhoneBook = new CiscoRoomOsPhonebookFolder("Top Level", string.Empty, string.Empty, []);
        _injestFolder = new Dictionary<string, string>();
        _injestContact = new Dictionary<string, string>();
        _injestContactMethods = [new()];

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
            if( !response.Contains("*r PhonebookSearchResult"))
                   return;
            
            if (response.Contains("status=OK"))
            {
                CommunicationState = CommunicationState.Error;
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
                            _currentRow = 1;
                            _currentSubRow = 1;
                            CommunicationState = CommunicationState.Okay;
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

                    if (loadResult.state == EntryLoadState.Loaded && loadResult.responseRow == _resultTotalRows)
                    {
                        if (_currentInjestfolder != null)
                            _currentInjestfolder.ContentsFetched = true;
                        RequestNextPhoneBookFolder();
                    }

                    CommunicationState = loadResult.state == EntryLoadState.Error
                        ? CommunicationState.Error
                        : CommunicationState.Okay;
                    return;
                }
                case "Contact":
                {
                    var loadResult = HandlePhonebookContactResponse(response, responses);

                    int resultRow = loadResult.responseRow + _resultOffset;

                    if (loadResult.state != EntryLoadState.Loaded)
                    {
                        CommunicationState = CommunicationState.Error;
                        return;
                    }

                    if (resultRow == _resultTotalRows)
                    {
                        _currentInjestfolder!.ContentsFetched = true;
                        RequestNextPhoneBookFolder();
                        CommunicationState = CommunicationState.Okay;
                        return;
                    }

                    if (loadResult.responseRow == _currentLimit)
                    {
                        if (_resultTotalRows == _currentLimit)
                        {
                            _currentInjestfolder!.ContentsFetched = true;
                            RequestNextPhoneBookFolder();
                            CommunicationState = CommunicationState.Okay;
                            return;
                        }

                        CommunicationClient.Send(
                            $"xCommand Phonebook Search PhonebookType: {_phonebookType} Offset:{_resultOffset + _currentLimit} FolderId: {_currentInjestfolder!.FolderId}\n");

                        CommunicationState = CommunicationState.Okay;
                        return;
                    }

                    break;
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
        _currentRow = 0;

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
        if (responseRow != _currentRow)
        {
            Log.Debug("Ignoring response as it's an invalid row, i'm expecting {CurrentRow}", _currentRow);
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
            []));

        _injestFolder.Clear();
        return (responseRow, EntryLoadState.Loaded);
    }

    private (int responseRow, EntryLoadState state) HandlePhonebookContactResponse(string response, string[] responses)
    {
        var responseRow = Int32.Parse(responses[3]);
        if (responseRow != _currentRow)
        {
            Log.Debug("Ignoring response as it's an invalid row, i'm expecting {CurrentRow}", _currentRow);
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
                var methodId = Int32.Parse(responses[5]);
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

        List<PhonebookNumber> contactMethods = [];

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
                    return;
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

    public override void PowerOn() => RequestPhonebook();

    public override void PowerOff() => RequestPhonebook();
}
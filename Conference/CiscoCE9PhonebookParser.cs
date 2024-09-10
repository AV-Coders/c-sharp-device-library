using AVCoders.Core;
using static System.Int32;

namespace AVCoders.Conference;

public delegate void CommsClientSend(string command);

public record CiscoRoomOsPhonebookFolder(
  string Name,
  string FolderId,
  string LocalId,
  List<PhonebookBase> Items,
  bool ContentsFetched = false)
  : PhonebookFolder(Name, Items)
{
  public bool ContentsFetched { get; set; }
}

public record CiscoRoomOsPhonebookContactMethod(string ContactMethodId, string Number, string Protocol, string CallRate): PhonebookNumber(Number);

public record CiscoRoomOsPhonebookContact(string Name, string ContactId, List<PhonebookNumber> ContactMethods): PhonebookContact(Name, ContactMethods);

public class CiscoCE9PhonebookParser
{
  private readonly string _phonebookType;

  private enum EntryLoadState
  {
    NotLoaded,
    Loaded,
    Error
  }
    public readonly CiscoRoomOsPhonebookFolder PhoneBook;
    public CommsClientSend Comms;
    public LogHandler? LogHandlers;
    
    // Phonebook parsing variables
    private Dictionary<string, string> _injestFolder;
    private Dictionary<string, string> _injestContact;
    private List<Dictionary<string, string>> _injestContactMethods;
    private int _currentRow;
    private int _currentSubRow;
    private int _resultOffset;
    private int _resultTotalRows;
    private PhonebookBase? _currentInjestfolder = null;
    private int _currentLimit = 50;

    public CiscoCE9PhonebookParser(string phonebookType = "Corporate")
    {
      _phonebookType = phonebookType;
      PhoneBook = new CiscoRoomOsPhonebookFolder("Top Level", String.Empty, String.Empty, new List<PhonebookBase>());
        _injestFolder = new Dictionary<string, string>();
        _injestContact = new Dictionary<string, string>();
        _injestContactMethods = new List<Dictionary<string, string>> { new ()};
        _currentRow = 1;
        _currentSubRow = 1;
    }

    public void RequestPhonebook()
    {
      if (Comms == null)
        throw new InvalidOperationException("A phonebook can't be requested without a send deleagte");
      
      Comms.Invoke($"xCommand Phonebook Search PhonebookType: {_phonebookType} Offset:0\n");
    }

    public CommunicationState HandlePhonebookSearchResponse(string response)
    {
      if (response.Contains("status=OK"))
        return CommunicationState.Error;
      
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
              return CommunicationState.Okay;
            case "TotalRows:":
              _resultTotalRows = Parse(responses[4]);
              Log($"Total rows is {_resultTotalRows}");
              _currentRow = 1;
              _currentSubRow = 1;
              return CommunicationState.Okay;
            case "Limit:":
              _currentLimit = Parse(responses[4]);
              return CommunicationState.Okay;
            default:
              Log($"Unhandled ResultInfo key: {responses[3]}");
              return CommunicationState.Error;
          }
          
          break;
        }
        case "Folder":
        {
          var loadResult = HandlePhonebookFolderResponse(response, responses);

          if (loadResult.state == EntryLoadState.Loaded && loadResult.responseRow == _resultTotalRows)
            RequestNextPhoneBookFolder();
        
          return loadResult.state == EntryLoadState.Error ? CommunicationState.Error : CommunicationState.Okay;
        }
        case "Contact":
        {
          var loadResult = HandlePhonebookContactResponse(response, responses);
        
          if (loadResult.state == EntryLoadState.Loaded && loadResult.responseRow == _resultTotalRows)
            RequestNextPhoneBookFolder();
        
          return loadResult.state == EntryLoadState.Error ? CommunicationState.Error : CommunicationState.Okay;
        }
      }

      Log($"Unhandled response key: {responses[2]}");
      return CommunicationState.Error;
    }

    private void RequestNextPhoneBookFolder()
    {
      if (_currentInjestfolder == null)
        PhoneBook.ContentsFetched = true;
      CiscoRoomOsPhonebookFolder? unFetchedFolder = FindUnFetchedFolder(PhoneBook.Items);
      Thread.Sleep(1000);
      if (unFetchedFolder == null)
      {
        Log("Phonebook search complete");
        return;
      }
      
      _currentInjestfolder = unFetchedFolder;
      
      Comms.Invoke($"xCommand Phonebook Search PhonebookType: {_phonebookType} Offset:0 FolderId: {unFetchedFolder.FolderId}\n");
    }

    private CiscoRoomOsPhonebookFolder? FindUnFetchedFolder(List<PhonebookBase> phoneBookItems)
    {
      foreach (PhonebookBase item in phoneBookItems)
      {
        if (item.GetType() == typeof(CiscoRoomOsPhonebookFolder))
        {
          CiscoRoomOsPhonebookFolder folder = (CiscoRoomOsPhonebookFolder) item;
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
      var  responseRow= Parse(responses[3]);
      if (responseRow != _currentRow)
      {
        Log("Ignoring response as it's an invalid row");
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
        Log("Ignoring response as it's an invalid row");
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
          if (methodId == 1 && _currentSubRow > 1)
          {
            _injestContactMethods.Clear();
            _injestContactMethods.Add(new Dictionary<string, string>());
            _currentSubRow = 1;
          }

          if (methodId > _currentRow)
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

      if (_injestContact.ContainsKey("Name:") &&
          _injestContact.ContainsKey("ContactId:") && 
          AllContactMethodsArePopulated()
          )
      {
        _currentRow++;
        _currentSubRow = 1;

        List<PhonebookNumber> contactMethods = new List<PhonebookNumber>();
        
        _injestContactMethods.ForEach(contactMethod =>
        {
          contactMethods.Add(new CiscoRoomOsPhonebookContactMethod(
            contactMethod["ContactMethodId:"],
            contactMethod["Number:"],
            contactMethod["Protocol:"],
            contactMethod["CallRate:"]));
        });

        AddContactToFolder(new CiscoRoomOsPhonebookContact(_injestContact["Name:"], _injestContact["ContactId:"], contactMethods));
        
        _injestContact.Clear();
        _injestContactMethods.Clear();
        return (responseRow, EntryLoadState.Loaded);
      }

      return (responseRow, EntryLoadState.NotLoaded);
    }

    private void AddContactToFolder(PhonebookBase contact)
    {
      if (_currentInjestfolder == null)
      {
        PhoneBook.Items.Add(contact);
        return;
      }
      
      PhoneBook.Items.ForEach(item => {
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
      foreach (var dictionaryEntry in _injestContactMethods)
      {
        if (!dictionaryEntry.ContainsKey("ContactMethodId:"))
          return false;
        if (!dictionaryEntry.ContainsKey("Number:"))
          return false;
        if (!dictionaryEntry.ContainsKey("Protocol:"))
          return false;
        if (!dictionaryEntry.ContainsKey("CallRate:"))
          return false;
      }

      return true;
    }

    private void Log(string message) => LogHandlers?.Invoke($"{GetType()} - {message}");
    
}
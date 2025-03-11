using AVCoders.CommunicationClients;
using Moq;

namespace AVCoders.Conference.Tests;

public class CiscoCollaborationEndpoint9PhonebookParserTest
{
    private readonly CiscoCollaborationEndpoint9PhonebookParser _parser;
    private readonly Mock<AvCodersTcpClient> _mockClient;
    
    public CiscoCollaborationEndpoint9PhonebookParserTest()
    {
        _mockClient = new Mock<AvCodersTcpClient>("Foo", (ushort) 1, "Bar");
        _parser = new CiscoCollaborationEndpoint9PhonebookParser(_mockClient.Object);
    }

    [Fact]
    public void PhonebookFolders_AreParsedCorrectly()
    {
        new List<string>
        {
            "*r PhonebookSearchResult (status=OK):", 
            "*r PhonebookSearchResult ResultInfo Offset: 0",
            "*r PhonebookSearchResult ResultInfo Limit: 20",
            "*r PhonebookSearchResult ResultInfo TotalRows: 13",
            "*r PhonebookSearchResult Folder 1 LocalId: \"c_61\"",
            "*r PhonebookSearchResult Folder 1 FolderId: \"c_61\"",
            "*r PhonebookSearchResult Folder 1 Name: \"AV Coders\"",
            "*r PhonebookSearchResult Folder 2 LocalId: \"c_35\"",
            "*r PhonebookSearchResult Folder 2 FolderId: \"c_35\"",
            "*r PhonebookSearchResult Folder 2 Name: \"A new folder with spaces\"",
            "*r PhonebookSearchResult Folder 3 LocalId: \"c_63\"",
            "*r PhonebookSearchResult Folder 3 FolderId: \"c_63\"",
            "*r PhonebookSearchResult Folder 3 Name: \"More folders\"",
        }.ForEach(response => _mockClient.Object.ResponseHandlers!.Invoke($"{response}\n"));

        CiscoRoomOsPhonebookFolder firstFolder = (CiscoRoomOsPhonebookFolder)_parser.PhoneBook.Items[0];
        
        Assert.Equal(3, _parser.PhoneBook.Items.Count);
        Assert.Equal("AV Coders", firstFolder.Name);
        Assert.Equal("c_61", firstFolder.LocalId);
        Assert.Equal("c_61", firstFolder.FolderId);
        Assert.Equal("A new folder with spaces", _parser.PhoneBook.Items[1].Name);
        Assert.Equal("More folders", _parser.PhoneBook.Items[2].Name);
    }

    [Fact]
    public void PhonebookContacts_AreParsedCorrectly()
    {
        new List<string>
        {
        "*r PhonebookSearchResult (status=OK):", 
        "*r PhonebookSearchResult ResultInfo Offset: 0",
        "*r PhonebookSearchResult ResultInfo Limit: 1000",
        "*r PhonebookSearchResult ResultInfo TotalRows: 109",
        "*r PhonebookSearchResult Contact 1 Name: \"Jeoffery Sparnston\"",
        "*r PhonebookSearchResult Contact 1 ContactId: \"e_55246\"",
        "*r PhonebookSearchResult Contact 1 ContactMethod 1 ContactMethodId: \"1\"",
        "*r PhonebookSearchResult Contact 1 ContactMethod 1 Number: \"SIP:123@234.com\"",
        "*r PhonebookSearchResult Contact 1 ContactMethod 1 Protocol: SIP",
        "*r PhonebookSearchResult Contact 1 ContactMethod 1 CallRate: 768",
        "*r PhonebookSearchResult Contact 2 Name: \"Storbven La Trattore\"",
        "*r PhonebookSearchResult Contact 2 ContactId: \"e_55247\"",
        "*r PhonebookSearchResult Contact 2 ContactMethod 1 ContactMethodId: \"1\"",
        "*r PhonebookSearchResult Contact 2 ContactMethod 1 Number: \"SIP:930293@thebestsipever.au\"",
        "*r PhonebookSearchResult Contact 2 ContactMethod 1 Protocol: SIP",
        "*r PhonebookSearchResult Contact 2 ContactMethod 1 CallRate: 768",
        "*r PhonebookSearchResult Contact 3 Name: \"Foo McBar\"",
        "*r PhonebookSearchResult Contact 3 ContactId: \"e_55249\"",
        "*r PhonebookSearchResult Contact 3 ContactMethod 1 ContactMethodId: \"1\"",
        "*r PhonebookSearchResult Contact 3 ContactMethod 1 Number: \"SIP:foomcbar@mcbarindustries.co.uk\"",
        "*r PhonebookSearchResult Contact 3 ContactMethod 1 Protocol: SIP",
        "*r PhonebookSearchResult Contact 3 ContactMethod 1 CallRate: 768",
        }.ForEach(response => _mockClient.Object.ResponseHandlers!.Invoke($"{response}\n"));
        
        CiscoRoomOsPhonebookContact firstContact = (CiscoRoomOsPhonebookContact)_parser.PhoneBook.Items[0];
        
        Assert.Equal(3, _parser.PhoneBook.Items.Count);
        Assert.Equal("Jeoffery Sparnston", firstContact.Name);
        Assert.Equal("e_55246", firstContact.ContactId);
        Assert.Equal("SIP:123@234.com", firstContact.ContactMethods[0].Number);
        Assert.Equal("Storbven La Trattore", _parser.PhoneBook.Items[1].Name);
        Assert.Equal("Foo McBar", _parser.PhoneBook.Items[2].Name);
    }
}
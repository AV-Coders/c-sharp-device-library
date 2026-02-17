using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Conference.Tests;

public class CiscoRoomOsPhonebookParserTest
{
    private readonly CiscoRoomOsPhonebookParser _parser;
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    
    public CiscoRoomOsPhonebookParserTest()
    {
        _parser = new CiscoRoomOsPhonebookParser(_mockClient.Object);
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
            "** end"
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
        "** end"
        }.ForEach(response => _mockClient.Object.ResponseHandlers!.Invoke($"{response}\n"));
        
        CiscoRoomOsPhonebookContact firstContact = (CiscoRoomOsPhonebookContact)_parser.PhoneBook.Items[0];
        
        Assert.Equal(3, _parser.PhoneBook.Items.Count);
        Assert.Equal("Jeoffery Sparnston", firstContact.Name);
        Assert.Equal("e_55246", firstContact.ContactId);
        Assert.Equal("SIP:123@234.com", firstContact.ContactMethods[0].Number);
        Assert.Equal("Storbven La Trattore", _parser.PhoneBook.Items[1].Name);
        Assert.Equal("Foo McBar", _parser.PhoneBook.Items[2].Name);
    }

    [Fact]
    public void PhonebookFolders_OutOfOrder_AreParsedCorrectly()
    {
        new List<string>
        {
            "*r PhonebookSearchResult (status=OK):",
            "*r PhonebookSearchResult ResultInfo Offset: 0",
            "*r PhonebookSearchResult ResultInfo Limit: 20",
            "*r PhonebookSearchResult ResultInfo TotalRows: 2",
            "*r PhonebookSearchResult Folder 2 Name: \"Second Folder\"",
            "*r PhonebookSearchResult Folder 1 Name: \"First Folder\"",
            "*r PhonebookSearchResult Folder 2 LocalId: \"c_2\"",
            "*r PhonebookSearchResult Folder 1 LocalId: \"c_1\"",
            "*r PhonebookSearchResult Folder 1 FolderId: \"c_1\"",
            "*r PhonebookSearchResult Folder 2 FolderId: \"c_2\"",
            "** end"
        }.ForEach(response => _mockClient.Object.ResponseHandlers!.Invoke($"{response}\n"));

        Assert.Equal(2, _parser.PhoneBook.Items.Count);
        
        var firstFolder = (CiscoRoomOsPhonebookFolder)_parser.PhoneBook.Items.FirstOrDefault(i => i.Name == "First Folder");
        var secondFolder = (CiscoRoomOsPhonebookFolder)_parser.PhoneBook.Items.FirstOrDefault(i => i.Name == "Second Folder");
        
        Assert.NotNull(firstFolder);
        Assert.Equal("c_1", firstFolder.LocalId);
        Assert.NotNull(secondFolder);
        Assert.Equal("c_2", secondFolder.LocalId);
    }

    [Fact]
    public void PhonebookContacts_Interleaved_AreParsedCorrectly()
    {
        new List<string>
        {
            "*r PhonebookSearchResult (status=OK):",
            "*r PhonebookSearchResult ResultInfo Offset: 0",
            "*r PhonebookSearchResult ResultInfo Limit: 1000",
            "*r PhonebookSearchResult ResultInfo TotalRows: 2",
            "*r PhonebookSearchResult Contact 1 Name: \"Contact One\"",
            "*r PhonebookSearchResult Contact 2 Name: \"Contact Two\"",
            "*r PhonebookSearchResult Contact 1 ContactId: \"e_1\"",
            "*r PhonebookSearchResult Contact 2 ContactId: \"e_2\"",
            "*r PhonebookSearchResult Contact 1 ContactMethod 1 ContactMethodId: \"1\"",
            "*r PhonebookSearchResult Contact 2 ContactMethod 1 ContactMethodId: \"1\"",
            "*r PhonebookSearchResult Contact 1 ContactMethod 1 Number: \"111\"",
            "*r PhonebookSearchResult Contact 2 ContactMethod 1 Number: \"222\"",
            "*r PhonebookSearchResult Contact 1 ContactMethod 1 Protocol: SIP",
            "*r PhonebookSearchResult Contact 2 ContactMethod 1 Protocol: SIP",
            "** end"
        }.ForEach(response => _mockClient.Object.ResponseHandlers!.Invoke($"{response}\n"));

        Assert.Equal(2, _parser.PhoneBook.Items.Count);

        var c1 = (CiscoRoomOsPhonebookContact)_parser.PhoneBook.Items.FirstOrDefault(i => i.Name == "Contact One");
        var c2 = (CiscoRoomOsPhonebookContact)_parser.PhoneBook.Items.FirstOrDefault(i => i.Name == "Contact Two");

        Assert.NotNull(c1);
        Assert.Equal("e_1", c1.ContactId);
        Assert.Equal("111", c1.ContactMethods[0].Number);

        Assert.NotNull(c2);
        Assert.Equal("e_2", c2.ContactId);
        Assert.Equal("222", c2.ContactMethods[0].Number);
    }

    [Fact]
    public void PhonebookContacts_WithMultipleMethods_AreParsedCorrectly()
    {
        new List<string>
        {
            "*r PhonebookSearchResult (status=OK):",
            "*r PhonebookSearchResult ResultInfo Offset: 0",
            "*r PhonebookSearchResult ResultInfo Limit: 1000",
            "*r PhonebookSearchResult ResultInfo TotalRows: 1",
            "*r PhonebookSearchResult Contact 1 Name: \"Multi Method\"",
            "*r PhonebookSearchResult Contact 1 ContactId: \"e_1\"",
            "*r PhonebookSearchResult Contact 1 ContactMethod 2 ContactMethodId: \"2\"",
            "*r PhonebookSearchResult Contact 1 ContactMethod 2 Number: \"222\"",
            "*r PhonebookSearchResult Contact 1 ContactMethod 2 Protocol: SIP",
            "*r PhonebookSearchResult Contact 1 ContactMethod 1 ContactMethodId: \"1\"",
            "*r PhonebookSearchResult Contact 1 ContactMethod 1 Number: \"111\"",
            "*r PhonebookSearchResult Contact 1 ContactMethod 1 Protocol: H323",
            "** end"
        }.ForEach(response => _mockClient.Object.ResponseHandlers!.Invoke($"{response}\n"));

        Assert.Equal(1, _parser.PhoneBook.Items.Count);
        var contact = (CiscoRoomOsPhonebookContact)_parser.PhoneBook.Items[0];
        Assert.Equal(2, contact.ContactMethods.Count);
        
        var m1 = contact.ContactMethods.FirstOrDefault(m => m.Number == "111");
        var m2 = contact.ContactMethods.FirstOrDefault(m => m.Number == "222");
        
        Assert.NotNull(m1);
        Assert.Equal("H323", ((CiscoRoomOsPhonebookContactMethod)m1).Protocol);
        Assert.NotNull(m2);
        Assert.Equal("SIP", ((CiscoRoomOsPhonebookContactMethod)m2).Protocol);
    }
}
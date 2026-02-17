using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Conference.Tests;

public class CiscoRoomOsPhonebookParserIntegrationTest
{
    [Fact(Skip = "Only to run locally")]
    public async Task Recursive_FolderDiscovery_And_Pagination_Test()
    {
        // 1. Setup
        var mockClient = TestFactory.CreateTcpClient();
        var mockPhonebookUpdated = new Mock<PhonebookUpdated>();
        var parser = new CiscoRoomOsPhonebookParser(mockClient.Object);
        parser.PhonebookUpdated = mockPhonebookUpdated.Object;
        
        // Setup the Mock to respond to specific commands sent by the parser
        mockClient.Setup(c => c.Send(It.IsAny<string>()))
                  .Callback<string>(cmd =>
                  {
                      var command = cmd.Trim();
                      string? fileName = command switch
                      {
                          "xCommand Phonebook Search PhonebookType: Corporate Offset:0 Limit: 300" => "Root.txt",
                          "xCommand Phonebook Search PhonebookType: Corporate Offset:0 FolderId: c_123 Limit: 300" => "Folder1.txt",
                          "xCommand Phonebook Search PhonebookType: Corporate Offset:0 FolderId: c_124 Limit: 300" => "Folder2.txt",
                          "xCommand Phonebook Search PhonebookType: Corporate Offset:0 FolderId: c_104 Limit: 300" => "Folder3.txt",
                          "xCommand Phonebook Search PhonebookType: Corporate Offset:0 FolderId: c_446 Limit: 300" => "Folder4.txt",
                          "xCommand Phonebook Search PhonebookType: Corporate Offset:0 FolderId: c_950 Limit: 300" => "Folder5.txt",
                          _ => null
                      };

                      if (fileName != null)
                      {
                          var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", fileName);
                          var responses = File.ReadAllLines(filePath);
                          foreach (var line in responses) mockClient.Object.ResponseHandlers?.Invoke($"{line}\n");
                      }
                      else
                      {
                          System.Console.WriteLine($"[DEBUG_LOG] Unrecognized command: '{command}'");
                          throw new InvalidOperationException($"[FATAL] Unrecognized command: '{command}'");
                      }
                  });

        // 2. Trigger Connection
        mockClient.Object.ConnectionStateHandlers?.Invoke(ConnectionState.Connected);
        
        // Wait for the 5s delay and the full chain of automated requests/responses to complete
        await Task.Delay(6000);

        // 3. Verification
        var rootFolder = (CiscoRoomOsPhonebookFolder)parser.PhoneBook;
        var folder1 = (CiscoRoomOsPhonebookFolder) rootFolder.Items.First();
        var folder2 = (CiscoRoomOsPhonebookFolder) rootFolder.Items[1];
        var folder3 = (CiscoRoomOsPhonebookFolder) rootFolder.Items[2];
        var folder4 = (CiscoRoomOsPhonebookFolder) rootFolder.Items[3];
        var folder5 = (CiscoRoomOsPhonebookFolder) rootFolder.Items[4];

        // Folder 1
        Assert.NotNull(folder1);
        Assert.Equal(153, folder1.Items.Count);
        Assert.Equal("Delhi Studio", folder1.Items.First().Name);
        Assert.Equal("Male Conference Room", folder1.Items.Last().Name);
        
        // Folder 2
        Assert.NotNull(folder2);
        Assert.Equal(208, folder2.Items.Count);
        Assert.Equal("Tirana Botanical Garden", folder2.Items.First().Name);
        Assert.Equal("Zurich Wildlife Sanctuary", folder2.Items.Last().Name);
        
        // Folder 3
        Assert.NotNull(folder3);
        Assert.Equal(75, folder3.Items.Count);
        Assert.Equal("Dynamic Forest", folder3.Items.First().Name);
        Assert.Equal("Proud Bridge", folder3.Items.Last().Name);
        
        // Folder 4
        Assert.NotNull(folder4);
        Assert.Equal(20, folder4.Items.Count);
        Assert.Equal("Magic Kingdom", folder4.Items.First().Name);
        Assert.Equal("De Efteling", folder4.Items.Last().Name);
        
        // Folder 5
        Assert.NotNull(folder5);
        Assert.Equal(4, folder5.Items.Count);
        Assert.Equal("Lake Superior", folder5.Items.First().Name);
        Assert.Equal("Lake Michigan", folder5.Items.Last().Name);
        
        mockPhonebookUpdated.Verify(x => x.Invoke(It.IsAny<PhonebookFolder>()), Times.Once);
        
    }
}

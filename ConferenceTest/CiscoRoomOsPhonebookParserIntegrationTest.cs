using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Conference.Tests;

public class CiscoRoomOsPhonebookParserIntegrationTest
{
    private static readonly Dictionary<string, string> CapturedReplies = new()
    {
        { "xCommand Phonebook Search PhonebookType: Corporate Offset:0 Limit: 300", "Root.txt" },
        { "xCommand Phonebook Search PhonebookType: Corporate Offset:0 FolderId: c_123 Limit: 300", "Folder1.txt" },
        { "xCommand Phonebook Search PhonebookType: Corporate Offset:0 FolderId: c_124 Limit: 300", "Folder2.txt" },
        { "xCommand Phonebook Search PhonebookType: Corporate Offset:0 FolderId: c_104 Limit: 300", "Folder3.txt" },
        { "xCommand Phonebook Search PhonebookType: Corporate Offset:0 FolderId: c_446 Limit: 300", "Folder4.txt" },
        { "xCommand Phonebook Search PhonebookType: Corporate Offset:0 FolderId: c_950 Limit: 300", "Folder5.txt" },
    };

    [Fact]
    public async Task LoginSuccessful_LoadsEveryFolderFromTheCapturedReplies()
    {
        var mockClient = TestFactory.CreateTcpClient();
        var phonebookUpdated = new Mock<PhonebookUpdated>();
        var parser = new CiscoRoomOsPhonebookParser(mockClient.Object);
        parser.PhonebookUpdated = phonebookUpdated.Object;
        mockClient.Setup(c => c.Send(It.IsAny<string>()))
            .Callback<string>(command =>
            {
                if (!CapturedReplies.TryGetValue(command.Trim(), out var fileName))
                    throw new InvalidOperationException($"Unrecognised command: '{command.Trim()}'");
                foreach (var line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "TestData", fileName)))
                    mockClient.Object.ResponseHandlers?.Invoke($"{line}\n");
            });

        mockClient.Object.ResponseHandlers?.Invoke("*r Login successful\n");

        await Wait.UntilAsync(() => phonebookUpdated.Invocations.Count > 0, "The phonebook was never updated");
        var rootFolder = (CiscoRoomOsPhonebookFolder)parser.PhoneBook;
        var folder1 = (CiscoRoomOsPhonebookFolder)rootFolder.Items[0];
        var folder2 = (CiscoRoomOsPhonebookFolder)rootFolder.Items[1];
        var folder3 = (CiscoRoomOsPhonebookFolder)rootFolder.Items[2];
        var folder4 = (CiscoRoomOsPhonebookFolder)rootFolder.Items[3];
        var folder5 = (CiscoRoomOsPhonebookFolder)rootFolder.Items[4];

        Assert.Equal(153, folder1.Items.Count);
        Assert.Equal("Delhi Studio", folder1.Items.First().Name);
        Assert.Equal("Male Conference Room", folder1.Items.Last().Name);
        Assert.Equal(208, folder2.Items.Count);
        Assert.Equal("Tirana Botanical Garden", folder2.Items.First().Name);
        Assert.Equal("Zurich Wildlife Sanctuary", folder2.Items.Last().Name);
        Assert.Equal(75, folder3.Items.Count);
        Assert.Equal("Dynamic Forest", folder3.Items.First().Name);
        Assert.Equal("Proud Bridge", folder3.Items.Last().Name);
        Assert.Equal(20, folder4.Items.Count);
        Assert.Equal("Magic Kingdom", folder4.Items.First().Name);
        Assert.Equal("De Efteling", folder4.Items.Last().Name);
        Assert.Equal(4, folder5.Items.Count);
        Assert.Equal("Lake Superior", folder5.Items.First().Name);
        Assert.Equal("Lake Michigan", folder5.Items.Last().Name);
        phonebookUpdated.Verify(x => x.Invoke(It.IsAny<PhonebookFolder>()), Times.Once);
    }
}

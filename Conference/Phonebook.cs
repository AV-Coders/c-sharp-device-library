using AVCoders.Core;

namespace AVCoders.Conference;

public record PhonebookBase(string Name);

public record PhonebookNumber(string Number);

public record PhonebookFolder(string Name, string FolderId, List<PhonebookBase> Items) : PhonebookBase(Name)
{
    public bool ContentsFetched { get; set; }
}

public record PhonebookContact(string Name, List<PhonebookNumber> Numbers): PhonebookBase(Name);

public delegate void PhonebookUpdated(PhonebookFolder folder);

public abstract class PhonebookParserBase : DeviceBase
{
    protected PhonebookParserBase(string name, CommunicationClient comms) : base(name, comms)
    {
    }

    public PhonebookFolder PhoneBook { get; protected set; }
    public PhonebookUpdated? PhonebookUpdated { get; set; }
    
    public abstract void RequestPhonebook();
}
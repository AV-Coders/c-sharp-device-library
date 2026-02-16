using AVCoders.Core;

namespace AVCoders.Conference;

public record PhonebookBase(string Name);

public record PhonebookNumber(string Number);

public record PhonebookFolder(string Name, List<PhonebookBase> Items): PhonebookBase(Name);

public record PhonebookContact(string Name, List<PhonebookNumber> Numbers): PhonebookBase(Name);

public delegate void PhonebookUpdated(PhonebookFolder folder);

public abstract class PhonebookParserBase : DeviceBase
{
    protected PhonebookParserBase(string name, CommunicationClient comms) : base(name, comms)
    {
    }

    public PhonebookUpdated? PhonebookUpdated { get; set; }
}
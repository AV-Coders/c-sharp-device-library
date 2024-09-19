namespace AVCoders.Conference;

public enum EntryLoadState
{
    NotLoaded,
    Loading,
    Loaded,
    Error
}

public delegate void PhonebookLoadStateHandler(EntryLoadState state);
namespace AirPdf.App.Services;

/// <summary>What the reader chose when a document with unsaved annotations is about to close.</summary>
public enum UnsavedChangesChoice
{
    /// <summary>Keep the document open. This is the window-close default too.</summary>
    Cancel = 0,

    /// <summary>Write the changes into the document before closing.</summary>
    Save = 1,

    /// <summary>Close and throw the changes away.</summary>
    Discard = 2
}

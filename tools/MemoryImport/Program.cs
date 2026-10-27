using Chatcheology.Data.Memories;
using Chatcheology.Tools.MemoryImport;

return MemoryImportCommand.Run(args, Console.Out, Console.Error, () => DateTime.UtcNow, () => new HttpsMemoryImageSource());

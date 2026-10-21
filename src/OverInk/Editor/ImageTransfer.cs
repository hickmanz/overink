using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace OverInk.Editor;

public static class ImageTransfer
{
    public static DataObject BuildDataObject(BitmapSource flattened, string filePath)
    {
        var pngStream = EncodePng(flattened);
        var obj = new DataObject();
        obj.SetData("PNG", pngStream, autoConvert: false);
        obj.SetImage(flattened);
        obj.SetFileDropList(new StringCollection { filePath });
        return obj;
    }

    /// <summary>
    /// Like <see cref="BuildDataObject"/>, for an image that was just saved to <paramref name="filePath"/>: the
    /// PNG flavor reuses the file's bytes instead of encoding the same pixels again.
    /// </summary>
    public static DataObject BuildDataObjectFromSavedFile(BitmapSource flattened, string filePath)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(filePath);
        }
        catch (IOException)
        {
            return BuildDataObject(flattened, filePath);
        }

        var obj = new DataObject();
        obj.SetData("PNG", new MemoryStream(bytes), autoConvert: false);
        obj.SetImage(flattened);
        obj.SetFileDropList(new StringCollection { filePath });
        return obj;
    }

    public static void SetClipboardWithRetry(DataObject data, int attempts = 8)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (COMException) when (i < attempts - 1)
            {
                Thread.Sleep(50);
            }
        }
    }

    public static MemoryStream EncodePng(BitmapSource bitmap)
    {
        var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
        stream.Position = 0;
        return stream;
    }
}

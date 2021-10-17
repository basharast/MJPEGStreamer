using System.Text;
using System.IO;
using Windows.Storage.Streams;
using System.Diagnostics;
using System;

namespace MJPEGStreamer
{

    sealed partial class MjpegHttpStreamer
    {

        private static byte[] CRLF = new byte[] { 13, 10 };
        private static byte[] EmptyLine = new byte[] { 13, 10, 13, 10};

        public MjpegHttpStreamer(Stream tcpSocketStream)
        {
            _httpSocketStream = tcpSocketStream;
        }

        public const string _boundary = "MJPEGStreamerboundary";
        public Stream _httpSocketStream = null; 

        public void WriteMJpegHeader()
        {
            try
            {
                Write(
                    "HTTP/1.0 200 OK\r\n" +
                    //"Pragma: no-cache\r\n" + 
                    "Server: MJPEGStreamer/0.0.1\r\n" +
                    //"cache-control: private, max-age=0, no-cache, no-store\r\n" + 
                    "Content-Type: multipart/x-mixed-replace;boundary=" + _boundary + "\r\n" +
                    "\r\n--" + _boundary + "\r\n"
                 );

                this._httpSocketStream.Flush();
            }catch(Exception ex)
            {

            }
       }

        public void WriteJpegHeader()
        {
            try
            {
                Write(
                    "HTTP/1.0 200 OK\r\n" +
                    "Pragma: no-cache\r\n" +
                    "Server: MJPEGStreamer/0.0.1\r\n" +
                    "cache-control: private, max-age=0, no-cache, no-store\r\n" +
                    "Content-Type: image/jpeg\r\n"
                 );

                this._httpSocketStream.Flush();
            }
            catch (Exception ex)
            {

            }
        }

        public void WriteErrorHeader()
        {
           
            try
            {
                Write("HTTP/1.0 404 NotFound\r\n\r\n");
                _httpSocketStream.Flush();
                _httpSocketStream.Dispose();
            }catch(Exception ex)
            {

            }
        }

        public void WriteMJpeg(InMemoryRandomAccessStream imageStream)
        {

            try
            {
                StringBuilder sb = new StringBuilder();

                sb.AppendLine("Mime-Type: image/jpeg");
                sb.AppendLine("Content-Type: image/jpeg");
                sb.AppendLine("Content-Length: " + imageStream.Size.ToString());
                sb.AppendLine();

                Write(sb.ToString());
                Stream s = imageStream.AsStreamForRead();
                //Debug.WriteLine("StreamForRead = " + s.Length);
                s.Position = 0;

                s.CopyTo(_httpSocketStream);
                Write("\r\n--" + _boundary + "\r\n");

                _httpSocketStream.Flush();
            }
            catch (Exception ex)
            {

            }
        }

        public void WriteJpeg(InMemoryRandomAccessStream imageStream)
        {

          
            try
            {
                Write("Content-Length: " + imageStream.Size.ToString() + "\r\n\r\n");
                Stream s = imageStream.AsStreamForRead();
                s.Position = 0;
                s.CopyTo(_httpSocketStream);
                _httpSocketStream.Flush();
                _httpSocketStream.Dispose();
            }catch(Exception ex)
            {

            }
        }

        private void Write(string text)
        {
            try
            {
                byte[] data = BytesOf(text);
                _httpSocketStream.Write(data, 0, data.Length);
            }catch(Exception ex)
            {

            }
        }

        private static byte[] BytesOf(string text)
        {
            return Encoding.ASCII.GetBytes(text);
        }



        /*public static string ReadRequest(Stream inputStream, int length)
        {

            byte[] data = new byte[length];
            int count = inputStream.Read(data,0,data.Length);

            if (count != 0)
                return Encoding.ASCII.GetString(data, 0, count);

            return null;
        }*/

        #region IDisposable Members

        public void Dispose()
        {

            try
            {

                if (_httpSocketStream != null)
                    _httpSocketStream.Dispose();

            }
            finally
            {
                _httpSocketStream = null;
            }
        }

        #endregion
    }
}

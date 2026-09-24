using System.Net;

namespace PadForge.Services
{
    internal interface IWebControllerListener
    {
        bool IsListening { get; }
        void Start(string prefix);
        /// <summary>Registers one more prefix on a listener that is already
        /// running. Throws <see cref="HttpListenerException"/> when http.sys
        /// refuses it, and the listener keeps serving its existing prefixes.</summary>
        void AddPrefix(string prefix);
        HttpListenerContext GetContext();
        void Stop();
        void Close();
    }

    internal sealed class HttpWebControllerListener : IWebControllerListener
    {
        private readonly HttpListener _listener = new();
        public bool IsListening => _listener.IsListening;
        public void Start(string prefix)
        {
            _listener.Prefixes.Add(prefix);
            _listener.Start();
        }
        // On a started listener the collection registers the prefix with
        // http.sys at once and records it only after that succeeds, so a
        // refused prefix leaves the running ones untouched.
        public void AddPrefix(string prefix) => _listener.Prefixes.Add(prefix);
        public HttpListenerContext GetContext() => _listener.GetContext();
        public void Stop() => _listener.Stop();
        public void Close() => _listener.Close();
    }
}

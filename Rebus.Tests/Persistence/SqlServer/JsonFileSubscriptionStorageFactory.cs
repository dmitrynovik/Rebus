using System;
using System.IO;
using Rebus.Persistence.FileSystem;
using Rebus.Subscriptions;
using Rebus.Tests.Contracts.Subscriptions;

namespace Rebus.Tests.Persistence.SqlServer
{
    public class JsonFileSubscriptionStorageFactory : ISubscriptionStorageFactory
    {
        readonly string _xmlDataFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "subscriptions.json");
        
        public ISubscriptionStorage Create()
        {
            if (File.Exists(_xmlDataFilePath)) File.Delete(_xmlDataFilePath);

            var storage = new JsonFileSubscriptionStorage(_xmlDataFilePath);

            return storage;
        }

        public void Cleanup()
        {
            if (File.Exists(_xmlDataFilePath)) File.Delete(_xmlDataFilePath);
        }
    }
}
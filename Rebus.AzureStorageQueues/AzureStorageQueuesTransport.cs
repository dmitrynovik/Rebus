﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Queue;
using Newtonsoft.Json;
using Rebus.Bus;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.Transport;

namespace Rebus.AzureStorageQueues
{
    public class AzureStorageQueuesTransport : ITransport, IInitializable
    {
        static ILog _log;

        static AzureStorageQueuesTransport()
        {
            RebusLoggerFactory.Changed += f => _log = f.GetCurrentClassLogger();
        }

        readonly CloudStorageAccount _cloudStorageAccount;
        readonly string _inputQueueName;
        readonly CloudQueueClient _queueClient;
        readonly ConcurrentDictionary<string, CloudQueue> _queues = new ConcurrentDictionary<string, CloudQueue>();

        public AzureStorageQueuesTransport(string connectionString, string inputQueueName)
        {
            if (connectionString == null) throw new ArgumentNullException("connectionString");
            if (inputQueueName == null) throw new ArgumentNullException("inputQueueName");

            _inputQueueName = inputQueueName.ToLowerInvariant();
            _cloudStorageAccount = CloudStorageAccount.Parse(connectionString);
            _queueClient = _cloudStorageAccount.CreateCloudQueueClient();
        }

        public void CreateQueue(string address)
        {
            var queue = GetQueue(address);

            queue.CreateIfNotExists();
        }

        public async Task Send(string destinationAddress, TransportMessage message, ITransactionContext context)
        {
            context.OnCommitted(async () =>
            {
                var queue = GetQueue(destinationAddress);

                var messageId = Guid.NewGuid().ToString();
                var popReceipt = Guid.NewGuid().ToString();

                var cloudQueueMessage = Serialize(message, messageId, popReceipt);

                try
                {
                    await queue.AddMessageAsync(cloudQueueMessage);
                }
                catch (Exception exception)
                {
                    throw new ApplicationException(string.Format("Could not send message with ID {0} to '{1}'", cloudQueueMessage.Id, destinationAddress), exception);
                }
            });
        }

        public async Task<TransportMessage> Receive(ITransactionContext context)
        {
            var inputQueue = GetQueue(_inputQueueName);

            var cloudQueueMessage = await inputQueue.GetMessageAsync(TimeSpan.FromSeconds(1), new QueueRequestOptions(), new OperationContext());

            if (cloudQueueMessage == null) return null;

            context.OnCompleted(async () =>
            {
                await inputQueue.DeleteMessageAsync(cloudQueueMessage.Id, cloudQueueMessage.PopReceipt);
            });

            return Deserialize(cloudQueueMessage);
        }

        static CloudQueueMessage Serialize(TransportMessage message, string messageId, string popReceipt)
        {
            var cloudStorageQueueTransportMessage = new CloudStorageQueueTransportMessage
            {
                Headers = message.Headers, 
                Body = message.Body
            };

            var cloudQueueMessage = new CloudQueueMessage(messageId, popReceipt);
            cloudQueueMessage.SetMessageContent(JsonConvert.SerializeObject(cloudStorageQueueTransportMessage));
            return cloudQueueMessage;
        }

        static TransportMessage Deserialize(CloudQueueMessage cloudQueueMessage)
        {
            var cloudStorageQueueTransportMessage = JsonConvert.DeserializeObject<CloudStorageQueueTransportMessage>(cloudQueueMessage.AsString);

            return new TransportMessage(cloudStorageQueueTransportMessage.Headers, cloudStorageQueueTransportMessage.Body);
        }

        class CloudStorageQueueTransportMessage
        {
            public Dictionary<string,string> Headers { get; set; }
            public byte[] Body { get; set; }
        }

        public string Address
        {
            get { return _inputQueueName; }
        }

        public void Initialize()
        {
            CreateQueue(_inputQueueName);
        }

        CloudQueue GetQueue(string address)
        {
            return _queues.GetOrAdd(address, _ => _queueClient.GetQueueReference(address));
        }

        public void PurgeInputQueue()
        {
            var queue = GetQueue(_inputQueueName);

            if (!queue.Exists()) return;

            _log.Info("Purging storage queue '{0}' (purging by deleting all messages)", _inputQueueName);

            try
            {
                while (true)
                {
                    var messages = queue.GetMessages(10).ToList();

                    if (!messages.Any()) break;

                    Task.WaitAll(messages.Select(async message =>
                    {
                        await queue.DeleteMessageAsync(message);
                    }).ToArray());

                    _log.Debug("Deleted {0} messages from '{1}'", messages.Count, _inputQueueName);
                }
            }
            catch (Exception exception)
            {
                throw new ApplicationException("Could not purge queue", exception);
            }
        }
    }
}

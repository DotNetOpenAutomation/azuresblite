﻿/*
Copyright (c) 2015 Paolo Patierno

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using Amqp;
using Amqp.Types;
using ppatierno.AzureSBLite.Utility;
using System;
using System.Collections;

namespace ppatierno.AzureSBLite.Messaging.Amqp
{
    /// <summary>
    /// Message receiver for AMQP protocol
    /// </summary>
    internal sealed class AmqpMessageReceiver : MessageReceiver
    {
        // AMQP messaging factory
        private AmqpMessagingFactory factory;

        // session and link of AMQP protocol
        private Session session;
        private ReceiverLink link;

        // receive mode
        private ReceiveMode receiveMode;

        // collection with peeked message to complete/abandon with "lock token"
        private IDictionary peekedMessages;

        /// <summary>
        /// Construcotr
        /// </summary>
        /// <param name="factory">Messaging factory instance</param>
        /// <param name="path">Entity path</param>
        internal AmqpMessageReceiver(AmqpMessagingFactory factory, string path) 
            : this(factory, path, ReceiveMode.PeekLock)
        {

        }

        /// <summary>
        /// Construcotr
        /// </summary>
        /// <param name="factory">Messaging factory instance</param>
        /// <param name="path">Entity path</param>
        /// <param name="receiveMode">Receive mode</param>
        internal AmqpMessageReceiver(AmqpMessagingFactory factory, string path, ReceiveMode receiveMode)
            : base(factory, path)
        {
            this.factory = factory;
            this.receiveMode = receiveMode;

            this.peekedMessages = new Hashtable();
        }

        #region MessageReceiver ...

        internal override EventData ReceiveEventData()
        {
            if (this.factory.OpenConnection())
            {
                if (this.session == null)
                {
                    this.session = new Session(this.factory.Connection);
                    if ((this.StartOffset == null) || (this.StartOffset == string.Empty))
                    {
                        this.link = new ReceiverLink(this.session, "amqp-receive-link " + this.Path, this.Path);
                    }
                    else
                    {
                        Map filters = new Map();
                        filters.Add(new Symbol("apache.org:selector-filter:string"),
                                    new DescribedValue(
                                        new Symbol("apache.org:selector-filter:string"),
                                        "amqp.annotation.x-opt-offset > '" + this.StartOffset + "'"));

                        this.link = new ReceiverLink(this.session, "amqp-receive-link " + this.Path,
                                        new global::Amqp.Framing.Source()
                                        {
                                            Address = this.Path,
                                            FilterSet = filters
                                        }, null);
                    }
                }

                Message message = this.link.Receive();

                if (message != null)
                {
                    this.link.Accept(message);
                    return new EventData(message);
                }
                
                return null;
            }

            return null;
        }

        public override BrokeredMessage Receive()
        {
            if (this.factory.OpenConnection())
            {
                if (this.session == null)
                {
                    this.session = new Session(this.factory.Connection);
                    this.link = new ReceiverLink(this.session, "amqp-receive-link " + this.Path, this.Path);
                }

                this.link.SetCredit(1, false);
                Message message = this.link.Receive();

                if (message != null)
                {
                    BrokeredMessage brokeredMessage = new BrokeredMessage(message);

                    // accept message if receive and delete mode
                    if (this.receiveMode == ReceiveMode.ReceiveAndDelete)
                        this.link.Accept(message);
                    else
                    {
                        // get "lock token" and add message to peeked messages collection
                        // to enable complete or abandon in the future
                        brokeredMessage.LockToken = new Guid(message.DeliveryTag);
                        brokeredMessage.Receiver = this;
                        this.peekedMessages.Add(brokeredMessage.LockToken, message);
                    }
                    return brokeredMessage;
                }

                return null;
            }

            return null;
        }

        private void Outcome(Guid lockToken, bool accept)
        {
            if (this.peekedMessages.Contains(lockToken))
            {
                if (accept)
                    this.link.Accept((Message)this.peekedMessages[lockToken]);
                else
                    this.link.Release((Message)this.peekedMessages[lockToken]);

                this.peekedMessages.Remove(lockToken);
            }
        }

        public override void Complete(Guid lockToken)
        {
            this.Outcome(lockToken, true);
        }

        public override void Abandon(Guid lockToken)
        {
            this.Outcome(lockToken, false);
        }

        public override void OnMessage(OnMessageAction callback, OnMessageOptions options)
        {
            if (this.factory.OpenConnection())
            {
                if (this.session == null)
                {
                    this.session = new Session(this.factory.Connection);
                    this.link = new ReceiverLink(this.session, "amqp-receive-link " + this.Path, this.Path);
                }

                // start the message pump
                this.link.Start(1,
                    (r, m) =>
                    {
                        if (m != null)
                        {
                            BrokeredMessage brokeredMessage = new BrokeredMessage(m);
                            callback(brokeredMessage);

                            //  if autocomplete requested
                            if (options.AutoComplete)
                                r.Accept(m);
                        }
                    });
            }
        }

        #endregion

        #region ClientEntity ...

        public override void Close()
        {
            if (this.session != null)
            {
                this.link.Close();
                this.session.Close();
                this.isClosed = true;
            }
        }

        #endregion
    }
}

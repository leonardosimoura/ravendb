﻿using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using Raven.NewClient.Client.Blittable;
using Raven.NewClient.Client.Json;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Raven.NewClient.Client.Http;

namespace Raven.NewClient.Client.Commands
{
    public class BatchCommand : RavenCommand<BlittableArrayResult>, IDisposable
    {
        private readonly JsonOperationContext _context;
        private readonly BlittableJsonReaderObject[] _commands;
        private readonly BatchOptions _options;

        public BatchCommand(JsonOperationContext context, List<DynamicJsonValue> commands, BatchOptions options = null)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (commands == null)
                throw new ArgumentNullException(nameof(commands));

            _context = context;

            _commands = new BlittableJsonReaderObject[commands.Count];
            for (var i = 0; i < commands.Count; i++)
            {
                var command = commands[i];
                _commands[i] = _context.ReadObject(command, "command");
            }

            _options = options;
        }

        public override HttpRequestMessage CreateRequest(ServerNode node, out string url)
        {
            var request = new HttpRequestMessage
            {
                Method = HttpMethod.Post,
                Content = new BlittableJsonContent(stream =>
                {
                    using (var writer = new BlittableJsonTextWriter(_context, stream))
                    {
                        writer.WriteStartArray();
                        var first = true;
                        foreach (var command in _commands)
                        {
                            if (first == false)
                                writer.WriteComma();
                            first = false;

                            writer.WriteObject(command);
                        }
                        writer.WriteEndArray();
                    }
                })
            };

            var sb = new StringBuilder($"{node.Url}/databases/{node.Database}/bulk_docs");

            AppendOptions(sb);

            url = sb.ToString();

            return request;
        }

        public override void SetResponse(BlittableJsonReaderObject response, bool fromCache)
        {
            if (response == null)
                throw new InvalidOperationException($"Got null response from the server after doing a batch, something is very wrong. Probably a garbled response.");

            Result = JsonDeserializationClient.BlittableArrayResult(response);
        }

        private void AppendOptions(StringBuilder sb)
        {
            if (_options == null)
                return;

            sb.AppendLine("?");

            if (_options.WaitForReplicas)
            {
                sb.Append("&waitForReplicasTimeout=").Append(_options.WaitForReplicasTimeout);
                if (_options.ThrowOnTimeoutInWaitForReplicas)
                {
                    sb.Append("&throwOnTimeoutInWaitForReplicas=true");
                }
                sb.Append("&numberOfReplicasToWaitFor=");

                sb.Append(_options.Majority
                    ? "majority"
                    : _options.NumberOfReplicasToWaitFor.ToString());
            }

            if (_options.WaitForIndexes)
            {
                sb.Append("&waitForIndexesTimeout=").Append(_options.WaitForIndexesTimeout);
                if (_options.ThrowOnTimeoutInWaitForIndexes)
                {
                    sb.Append("&waitForIndexThrow=true");
                }
                if (_options.WaitForSpecificIndexes != null)
                {
                    foreach (var specificIndex in _options.WaitForSpecificIndexes)
                    {
                        sb.Append("&waitForSpecificIndexs=").Append(specificIndex);
                    }
                }
            }
        }

        public override bool IsReadRequest => false;

        public void Dispose()
        {
            foreach (var command in _commands)
                command?.Dispose();
        }
    }
}
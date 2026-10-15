using System.Text;
using System.Text.Json;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio.AskClient;

internal sealed class McsJsonActivityReader(Stream source, Action<string> serviceConversationId)
{
    private readonly Stream _source = source;
    private readonly Action<string> _serviceConversationId = serviceConversationId;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _bufferOffset;
    private int _bufferLength;
    private int _pushback = -1;
    private bool _started;
    private bool _activitiesRead;
    private bool _activitiesOpen;
    private bool _activityValueRead;
    private bool _rootCommaPending;
    private bool _finished;

    public async ValueTask<string?> ReadActivityAsync(CancellationToken cancellationToken)
    {
        if (_finished)
        {
            return null;
        }

        if (!_started)
        {
            _started = true;
            if (await ReadNonWhitespaceAsync(cancellationToken).ConfigureAwait(false) != '{')
            {
                throw new JsonException("The JSON response root must be an object.");
            }
        }

        while (true)
        {
            if (_activitiesOpen)
            {
                var item = await ReadNonWhitespaceAsync(cancellationToken).ConfigureAwait(false);
                if (_activityValueRead && item == ',')
                {
                    item = await ReadNonWhitespaceAsync(cancellationToken).ConfigureAwait(false);
                    if (item is -1 or ']')
                    {
                        throw new JsonException("The JSON activities array is malformed.");
                    }
                }
                else if (item == ']')
                {
                    _activitiesOpen = false;
                    _activityValueRead = false;
                    await ReadRootSeparatorAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                else if (item == -1)
                {
                    throw new JsonException("The JSON activities array was truncated.");
                }
                else if (_activityValueRead)
                {
                    throw new JsonException("The JSON activities array is missing a separator.");
                }

                var value = await ReadValueAsync(item, cancellationToken).ConfigureAwait(false);
                using (JsonDocument.Parse(value))
                {
                }

                _activityValueRead = true;
                return Encoding.UTF8.GetString(value);
            }

            var next = await ReadNonWhitespaceAsync(cancellationToken).ConfigureAwait(false);
            if (next == -1)
            {
                throw new JsonException("The JSON response ended before its root object.");
            }

            if (next == '}')
            {
                if (!_activitiesRead)
                {
                    throw new JsonException("The JSON response did not contain an activities array.");
                }

                if (_rootCommaPending)
                {
                    throw new JsonException("The JSON response contains a trailing root comma.");
                }

                if (await ReadNonWhitespaceAsync(cancellationToken).ConfigureAwait(false) != -1)
                {
                    throw new JsonException("The JSON response contains trailing content.");
                }

                _finished = true;
                return null;
            }

            if (next != '"')
            {
                throw new JsonException("The JSON response contains an invalid root property.");
            }

            _rootCommaPending = false;
            var property = await ReadStringAsync(next, cancellationToken).ConfigureAwait(false);
            if (await ReadNonWhitespaceAsync(cancellationToken).ConfigureAwait(false) != ':')
            {
                throw new JsonException("The JSON response contains an invalid property separator.");
            }

            var firstValue = await ReadNonWhitespaceAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(property, "activities", StringComparison.Ordinal))
            {
                if (_activitiesRead)
                {
                    throw new JsonException("The JSON response contains duplicate activities properties.");
                }

                _activitiesRead = true;
                if (firstValue != '[')
                {
                    throw new JsonException("The JSON activities property must be an array.");
                }

                _activitiesOpen = true;
                _activityValueRead = false;
                continue;
            }

            var valueBytes = await ReadValueAsync(firstValue, cancellationToken).ConfigureAwait(false);
            using (var document = JsonDocument.Parse(valueBytes))
            {
                if (string.Equals(property, "conversationId", StringComparison.Ordinal))
                {
                    if (document.RootElement.ValueKind == JsonValueKind.String)
                    {
                        var conversationId = document.RootElement.GetString();
                        if (!string.IsNullOrWhiteSpace(conversationId))
                        {
                            _serviceConversationId(conversationId);
                        }
                    }
                    else if (document.RootElement.ValueKind is not JsonValueKind.Null)
                    {
                        throw new JsonException("The JSON conversationId property must be a string or null.");
                    }
                }
            }

            await ReadRootSeparatorAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ReadRootSeparatorAsync(CancellationToken cancellationToken)
    {
        var separator = await ReadNonWhitespaceAsync(cancellationToken).ConfigureAwait(false);
        if (separator == ',')
        {
            _rootCommaPending = true;
            return;
        }

        _rootCommaPending = false;
        if (separator != -1)
        {
            _pushback = separator;
        }
    }

    private async ValueTask<byte[]> ReadValueAsync(int first, CancellationToken cancellationToken)
    {
        if (first == -1)
        {
            throw new JsonException("The JSON value was truncated.");
        }

        using var value = new MemoryStream();
        value.WriteByte((byte)first);

        if (first is '{' or '[')
        {
            var depth = 1;
            var inString = false;
            var escaped = false;
            while (depth > 0)
            {
                var next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                if (next == -1)
                {
                    throw new JsonException("The JSON value was truncated.");
                }

                value.WriteByte((byte)next);
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (next == '\\')
                    {
                        escaped = true;
                    }
                    else if (next == '"')
                    {
                        inString = false;
                    }
                }
                else if (next == '"')
                {
                    inString = true;
                }
                else if (next is '{' or '[')
                {
                    depth++;
                }
                else if (next is '}' or ']')
                {
                    depth--;
                }
            }
        }
        else if (first == '"')
        {
            var escaped = false;
            while (true)
            {
                var next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                if (next == -1)
                {
                    throw new JsonException("The JSON string was truncated.");
                }

                value.WriteByte((byte)next);
                if (escaped)
                {
                    escaped = false;
                }
                else if (next == '\\')
                {
                    escaped = true;
                }
                else if (next == '"')
                {
                    break;
                }
            }
        }
        else
        {
            while (true)
            {
                var next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                if (next == -1 || next is ' ' or '\t' or '\r' or '\n' or ',' or ']' or '}')
                {
                    if (next != -1)
                    {
                        _pushback = next;
                    }

                    break;
                }

                value.WriteByte((byte)next);
            }
        }

        return value.ToArray();
    }

    private async ValueTask<string> ReadStringAsync(int first, CancellationToken cancellationToken)
    {
        var bytes = await ReadValueAsync(first, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("The JSON property name is not a string.");
        }

        return document.RootElement.GetString()!;
    }

    private async ValueTask<int> ReadNonWhitespaceAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (next == -1 || next is not (' ' or '\t' or '\r' or '\n'))
            {
                return next;
            }
        }
    }

    private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_pushback >= 0)
        {
            var result = _pushback;
            _pushback = -1;
            return result;
        }

        if (_bufferOffset >= _bufferLength)
        {
            _bufferLength = await _source.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            _bufferOffset = 0;
            if (_bufferLength == 0)
            {
                return -1;
            }
        }

        return _buffer[_bufferOffset++];
    }
}

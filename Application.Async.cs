        private async Task ProcessRequestsAsync()
        {
            while (true)
            {
                var readResult = await Reader.ReadAsync();
                var buffer = readResult.Buffer;
                var isCompleted = readResult.IsCompleted;

                if (buffer.IsEmpty && isCompleted)
                {
                    return;
                }

                while (true)
                {
                    if (!ParseHttpRequest(ref buffer, isCompleted))
                    {
                        return;
                    }

                    if (_state == State.Body)
                    {
                        await ProcessRequestAsync();

                        _state = State.StartLine;

                        if (!buffer.IsEmpty)
                        {
                            // More input data to parse
                            continue;
                        }
                    }

                    // No more input or incomplete data, Advance the Reader
                    Reader.AdvanceTo(buffer.Start, buffer.End);
                    break;
                }

                await Writer.FlushAsync();
            }
        }

        private bool ParseHttpRequest(ref ReadOnlySequence<byte> buffer, bool isCompleted)
        {
            var reader = new SequenceReader<byte>(buffer);
            var state = _state;

            if (state == State.StartLine)
            {
                if (Parser.ParseRequestLine(new ParsingAdapter(this), ref reader))
                {
                    state = State.Headers;
                }
            }

            if (state == State.Headers)
            {
                var success = Parser.ParseHeaders(new ParsingAdapter(this), ref reader);

                if (success)
                {
                    state = State.Body;
                }
            }

            if (state != State.Body && isCompleted)
            {
                ThrowUnexpectedEndOfData();
            }

            _state = state;

            if (state == State.Body)
            {
                // Complete request read, consumed and examined are the same (length 0)
                buffer = buffer.Slice(reader.Position, 0);
            }
            else
            {
                // In-complete request read, consumed is current position and examined is the remaining.
                buffer = buffer.Slice(reader.Position);
            }

            return true;
        }
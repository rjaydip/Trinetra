# Written for the eventual deployment shape (brief §36's docker-compose ai-worker service).
# NOT used to build or run this pass — the application was built and validated directly against
# a local venv first, per the user's explicit instruction; Docker packaging comes later.
FROM python:3.11-slim

RUN apt-get update \
    && apt-get install -y --no-install-recommends libgl1 libglib2.0-0 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY . .

EXPOSE 8000

CMD ["python", "worker.py"]

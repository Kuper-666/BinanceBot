#!/usr/bin/env python3
"""
NewsSentinel Crypto News Fetcher
Запуск: python news_fetcher.py [--interval 3600] [--db-path Data/news.db]

Каждые N секунд (по умолчанию 3600 = 1 час) скачивает заголовки крипто-новостей
из бесплатных RSS-лент и сохраняет в SQLite для чтения C# ботом.

Зависимости: pip install feedparser requests
"""

import sqlite3
import time
import argparse
import os
import sys
import re
from datetime import datetime, timezone
from urllib.request import urlopen, Request
from urllib.error import URLError

try:
    import feedparser
    HAS_FEEDPARSER = True
except ImportError:
    HAS_FEEDPARSER = False

RSS_FEEDS = [
    "https://cointelegraph.com/rss",
    "https://cointelegraph.com/rss/tag/binance",
    "https://www.coindesk.com/arc/outboundfeeds/rss/",
    "https://cryptonews.com/news/feed/",
    "https://bitcoinmagazine.com/feed",
]

CRYPTO_KEYWORDS = [
    "bitcoin", "btc", "ethereum", "eth", "binance", "bnb",
    "crypto", "blockchain", "defi", "nft", "altcoin",
    "regulation", "sec", "cftc", "ban", "crash", "pump",
    "dump", "hack", "exploit", "bullish", "bearish",
    "rally", "correction", "moon", "whale", "institutional",
]

NEGATIVE_KEYWORDS = [
    "ban", "crash", "hack", "exploit", "scam", "fraud",
    "regulation", "sec", "lawsuit", "investigation",
    "collapse", "bankrupt", "bearish", "sell-off", "selloff",
    "dump", "plunge", "tumble", "downfall", "warning",
    "risk", "danger", "threat", "concern", "fear",
]

POSITIVE_KEYWORDS = [
    "rally", "bullish", "moon", "ath", "all-time high",
    "adoption", "institutional", "etf", "approval",
    "partnership", "launch", "upgrade", "milestone",
    "record", "growth", "surge", "breakout", "accumulate",
]

HIGH_IMPACT_KEYWORDS = [
    "sec", "ban", "regulation", "etf", "hack", "exploit",
    "crash", "lawsuit", "bankrupt", "institutional",
    "government", "federal", "congress", "executive order",
]


def init_db(db_path):
    os.makedirs(os.path.dirname(db_path) if os.path.dirname(db_path) else ".", exist_ok=True)
    conn = sqlite3.connect(db_path)
    conn.execute("""
        CREATE TABLE IF NOT EXISTS news (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            title TEXT NOT NULL,
            source TEXT,
            sentiment TEXT,
            impact INTEGER DEFAULT 0,
            symbols TEXT,
            fetched_at TEXT NOT NULL
        )
    """)
    conn.execute("CREATE INDEX IF NOT EXISTS idx_fetched_at ON news(fetched_at)")
    conn.commit()
    return conn


def analyze_sentiment(title):
    title_lower = title.lower()
    neg_score = sum(1 for kw in NEGATIVE_KEYWORDS if kw in title_lower)
    pos_score = sum(1 for kw in POSITIVE_KEYWORDS if kw in title_lower)

    if neg_score > pos_score:
        return "negative"
    elif pos_score > neg_score:
        return "positive"
    return "neutral"


def calculate_impact(title):
    title_lower = title.lower()
    score = sum(1 for kw in HIGH_IMPACT_KEYWORDS if kw in title_lower)
    return min(score, 5)


def extract_symbols(title):
    known = {
        "bitcoin": "BTC", "btc": "BTC",
        "ethereum": "ETH", "eth": "ETH",
        "binance": "BNB", "bnb": "BNB",
        "solana": "SOL", "sol": "SOL",
        "xrp": "XRP", "ripple": "XRP",
        "cardano": "ADA", "ada": "ADA",
        "dogecoin": "DOGE", "doge": "DOGE",
        "polkadot": "DOT", "dot": "DOT",
        "avalanche": "AVAX", "avax": "AVAX",
    }
    title_lower = title.lower()
    found = set()
    for kw, sym in known.items():
        if kw in title_lower:
            found.add(sym)
    return ",".join(found) if found else "*"


def fetch_from_rss(feed_url, timeout=15):
    """Fetch headlines from RSS feed using feedparser or fallback to urllib."""
    items = []
    try:
        if HAS_FEEDPARSER:
            feed = feedparser.parse(feed_url)
            for entry in feed.entries[:10]:
                title = entry.get("title", "")
                source = feed.feed.get("title", feed_url)
                if title:
                    items.append({"title": title, "source": source})
        else:
            req = Request(feed_url, headers={"User-Agent": "Mozilla/5.0"})
            with urlopen(req, timeout=timeout) as resp:
                data = resp.read().decode("utf-8", errors="ignore")
            titles = re.findall(r"<title[^>]*>(.*?)</title>", data, re.IGNORECASE | re.DOTALL)
            for t in titles[:10]:
                t = re.sub(r"<[^>]+>", "", t).strip()
                if t and len(t) > 10:
                    items.append({"title": t, "source": feed_url})
    except (URLError, Exception) as e:
        print(f"  Warning: Failed to fetch {feed_url}: {e}", file=sys.stderr)
    return items


def cleanup_old(conn, max_age_hours=48):
    cutoff = (datetime.now(timezone.utc)).strftime("%Y-%m-%dT%H:%M:%SZ")
    conn.execute("DELETE FROM news WHERE fetched_at < datetime(?, ?)",
                 (cutoff, f"-{max_age_hours} hours"))
    conn.commit()


def fetch_cycle(conn, existing_titles):
    """Run one fetch cycle across all RSS feeds."""
    new_count = 0
    for feed_url in RSS_FEEDS:
        print(f"  Fetching: {feed_url}")
        items = fetch_from_rss(feed_url)
        for item in items:
            title = item["title"]
            if title in existing_titles:
                continue

            sentiment = analyze_sentiment(title)
            impact = calculate_impact(title)
            symbols = extract_symbols(title)
            now = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

            try:
                conn.execute(
                    "INSERT INTO news (title, source, sentiment, impact, symbols, fetched_at) "
                    "VALUES (?, ?, ?, ?, ?, ?)",
                    (title, item["source"], sentiment, impact, symbols, now)
                )
                new_count += 1
                existing_titles.add(title)
            except sqlite3.Error as e:
                print(f"  DB error: {e}", file=sys.stderr)

    conn.commit()
    return new_count


def main():
    parser = argparse.ArgumentParser(description="Crypto News Fetcher for NewsSentinel")
    parser.add_argument("--interval", type=int, default=3600, help="Fetch interval in seconds (default: 3600)")
    parser.add_argument("--db-path", type=str, default="Data/news.db", help="SQLite database path")
    parser.add_argument("--once", action="store_true", help="Run once and exit")
    args = parser.parse_args()

    print(f"NewsSentinel Crypto News Fetcher")
    print(f"DB: {args.db_path}, Interval: {args.interval}s")
    print(f"Feedparser: {'available' if HAS_FEEDPARSER else 'not installed (using regex fallback)'}")
    print()

    conn = init_db(args.db_path)

    while True:
        print(f"[{datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M:%S UTC')}] Fetching news...")

        existing = set()
        try:
            cursor = conn.execute("SELECT title FROM news WHERE fetched_at >= datetime('now', '-6 hours')")
            existing = {row[0] for row in cursor.fetchall()}
        except sqlite3.Error:
            pass

        new_count = fetch_cycle(conn, existing)
        print(f"  New articles: {new_count}")

        cleanup_old(conn)

        stats = conn.execute(
            "SELECT sentiment, COUNT(*) FROM news WHERE fetched_at >= datetime('now', '-6 hours') GROUP BY sentiment"
        ).fetchall()
        for sent, cnt in stats:
            print(f"  {sent}: {cnt}")

        if args.once:
            break

        print(f"  Next fetch in {args.interval}s...")
        time.sleep(args.interval)


if __name__ == "__main__":
    main()

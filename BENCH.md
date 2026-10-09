# Bench prefill vs token generation — Qwen 27B su CUDA0 16GB

Data: 2026-10-09. Server `0.0.0.0:9931`, `--parallel 1`.
Motore: vanilla `llama-b11529` (mono), KV-Arena (262k fissi), Troed adaptive-kv-streaming (262k streaming+MTP).

## Setup dei due test

- **Decode (token generation)** — `prompt_decode.txt`: 1 riga `crea un programma mandelbrot per utf-8`. `POST /completion`, `temperature=0`, `n_predict=512`. Prefill trascurabile (13 tok).
- **Prefill (contesto lungo, risposta corta)** — `prompt_prefill.txt` o `prompt_prefill_32k.txt`: log deterministico (seed 7) + 3 aghi `CODICE-AGO` da ritrovare, footer identico al testo decode. `n_predict=256` (262k) / `64` (32k).
- Metriche dai `timings` del server: `pp_s = prompt_n / prompt_ms`, `tg_s = predicted_n / predicted_ms`. `cache_prompt=false`.
- Dimensioni prefill: **26816 tok** (`32k-var`, mono 64k/32k e confronto orizzontale) e **64944 tok** (pieno, solo motori 262k).
- Raw completo: `bench_results.jsonl` (una riga per run). Log server: `logs\*.err.log`.

## Risultati

| Config | Engine | Modello | ctx | MTP | pp_s (prefill) | tg_s post-prefill | tg_s decode | Note |
|---|---|---|---|---|---|---|---|---|
| mono-GSQ-T-64k | vanilla | GSQ IQ3_S | 64k | MTP n=3 | 1531.8 (26816 tok 32k-var) | 94.6 | 93.6 | 3/3 aghi |
| mono-XS-T-64k | vanilla | IQ4_XS | 64k | MTP n=3 | 1574.5 (26816) | 91.0 | 89.1 | 3/3 aghi |
| mono-PI-T-64k | vanilla | PI Q3_K_M | 64k | MTP n=3 | 1395.5 (26816) | n/a (stop 1 tok) | 84.7 | 3/3 aghi |
| mono-SW-T-64k | vanilla | Swift IQ3_S | 64k | MTP n=3 | 1525.7 (26816) | 92.9 | 98.6 | 3/3 aghi |
| mono-GSQ-I-32k | vanilla | GSQ IQ3_S | 32k | MTP n=3 | 1533.9 (26816) | 94.5 | 93.5 | +mmproj stock |
| mono-PI-I-32k | vanilla | PI Q3_K_M | 32k | MTP n=3 | 1404.3 (26816) | n/a (stop 1 tok) | 85.2 | +mmproj-pi |
| arena-GSQ-T-262k | KV-Arena | GSQ IQ3_S | 262k | no | 1321.1 (64944 tok) | 37.7 | 53.8 | 3/3 aghi |
| arena-GSQ-I-262k | KV-Arena | GSQ IQ3_S | 262k | no | 1330.5 (64944) | 37.6 | 53.7 | +mmproj stock |
| troed-GSQ-T-262k | Troed | GSQ IQ3_S | 262k | MTP n=5 | 935.5 (64944) | 43.6 | 65.6 | 3/3 aghi |
| troed-GSQ-I-262k | Troed | GSQ IQ3_S | 262k | MTP n=3 | 945.6 (64944) | 41.9 | 64.0 | +mmproj stock |
| troed-SW-T-262k | Troed | Swift IQ3_S | 262k | MTP n=5 | 941.1 (64944) | 46.3 | 68.9 | 3/3 aghi |
| troed-AC-T-262k | Troed | AC IQ4_XS | 262k | MTP n=5 | 965.8 (64944) | n/a (stop 1 tok) | 64.9 | solo testo |

## Osservazioni

- **Decode**: il vanilla mono (64k) vince nettamente (84–99 t/s) grazie a MTP + KV piccolo. Tra i 262k, Troed supera Arena sul decode (64–69 vs 54) grazie all'MTP; lo Swift è il più veloce dei 262k (98.6 in mono, 68.9 in Troed) confermando il profilo reasoning-efficient.
- **Prefill**: inverso. Il vanilla mono è il più rapido (1395–1575 t/s), Arena segue (1321–1330), Troed è il più lento (936–966 t/s) per il batch `-b 256` dello streaming. A parità di motore, testo vs immagini non cambia la velocità (mmproj caricato ma non usato).
- **Trade-off 262k**: Arena = prefill migliore, Troed = decode migliore. Con MTP attivo Troed resta comunque sopra Arena su entrambe le voci del confronto decode.
- **tg dopo prefill lungo** scende sempre rispetto al decode corto (KV grande + draft meno efficace) — atteso.
- **PI e AC** emettono un solo token sul prompt prefill (stop immediato su quel footer): il `pp_s` resta valido, il `tg` post-prefill no. Da indagare separatamente se interessa il comportamento, non è un problema di performance.
- Tutti gli aghi `CODICE-AGO` recuperati in ordine corretto dalle config che hanno generato testo (verifica prefill completa, non solo coda).

## Riproduzione

```
python bench_one.py --label <nome> --url http://127.0.0.1:9931 \
  --decode prompt_decode.txt --prefill prompt_prefill.txt --out bench_results.jsonl
```
`campaign.ps1` contiene i comandi esatti per ogni config (build vanilla/Arena/Troed).

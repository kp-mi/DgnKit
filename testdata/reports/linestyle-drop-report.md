# DgnKit dash patterns vs MicroStation stroked line style geometry

| file | element | style | DgnKit pattern (UOR) | phase | MicroStation dashes | interior length match | position match |
|---|---|---|---|---|---|---|---|
| 05_XL7089_PS_SP1.dgn | 2260 | RD_BR_FenceNew | 166.667 41.667 | 145.833 | 22: 20.833 166.667 166.667 166.667 166.667 166.667 (first at 0) | yes | yes |
| 05_XL7089_PS_SP1.dgn | 2987 | UT_PW_AbvGndPower | 72.844 145.687 72.844 41.625 | 0 | 47: 72.844 72.844 72.844 72.844 72.844 72.844 (first at 0) | yes | yes |
| 05_XL7089_PS_SP1.dgn | 3000 | RD_BR_FenceNew | 166.667 41.667 | 145.833 | 6: 20.833 166.667 166.667 166.667 166.667 166.667 (first at 0) | yes | yes |
| 05_XL7089_PS_SP1.dgn | 3022 | RD_BR_FenceNew | 166.667 41.667 | 145.833 | 8: 20.833 166.667 166.667 166.667 166.667 166.667 (first at 0) | yes | yes |
| 05_XL7089_PS_SP1.dgn | 3034 | TP_NT_TreeLine | 364.583 52.083 | 182.292 | 4: 182.292 364.583 364.583 34.514 (first at 0) | yes | yes |
| 05_XL7089_PS_SP1.dgn | 3293 | UT_PW_AbvGndPower | 72.844 145.687 72.844 41.625 | 56.054 | 7: 16.789 72.844 72.844 72.844 72.844 72.844 (first at 0) | yes | yes |
| 05_XL7089_PS_SP1.dgn | 3313 | UT_PW_AbvGndPower | 72.844 145.687 72.844 41.625 | 0 | 47: 72.844 72.844 72.844 72.844 72.844 72.844 (first at 0) | yes | yes |
| 12_XL7089_PS_TC1.dgn | 31504 | RD_MK_LineSkipCenterNew | 83.333 250 | 0.356 | 2: 82.977 83.333 (first at 0) | yes | yes |
| 12_XL7089_PS_TC1.dgn | 31505 | RD_MK_LineSkipCenterNew | 83.333 250 | 0.356 | 36: 82.977 83.333 83.333 83.333 83.333 83.333 (first at 0) | yes | yes |
| 12_XL7089_PS_TC1.dgn | 31506 | RD_MK_LineSkipCenterNew | 83.333 250 | 0.356 | 36: 82.977 83.333 83.333 83.333 83.333 83.333 (first at 0) | yes | yes |
| 13_XL7089_PS_TC2.dgn | 9505 | RD_BR_BarrierCenter | 50 8.333 58.333 8.333 | 25 | 134: 25 58.333 50 58.333 50 58.333 (first at 0) | yes | yes |
| 13_XL7089_PS_TC2.dgn | 9524 | RD_BR_BarrierCenter | 50 8.333 58.333 8.333 | 25 | 13: 25 58.333 50 58.333 50 58.333 (first at 0) | yes | yes |
| 13_XL7089_PS_TC2.dgn | 10714 | RD_BR_BarrierCenter | 50 8.333 58.333 8.333 | 25 | 65: 25 58.333 50 58.333 50 58.333 (first at 0) | yes | yes |
| 13_XL7089_PS_TC2.dgn | 20158 | RD_MK_LineSkipCenterNew | 100000 300000 | 0 | 1: 100000 (first at 0) | n/a | yes |
| 13_XL7089_PS_TC2.dgn | 20159 | RD_MK_LineSkipCenterNew | 100000 300000 | 0 | 1: 100000 (first at 0) | n/a | yes |
| 13_XL7089_PS_TC2.dgn | 20173 | RD_MK_LineSolidNew | solid | | 134 pieces → 1 run(s) | yes (continuous) | |
| 13_XL7089_PS_TC2.dgn | 20174 | RD_MK_LineSolidNew | solid | | 37 pieces → 1 run(s) | yes (continuous) | |
| 13_XL7089_PS_TC2.dgn | 20176 | RD_MK_LineSolidNew | solid | | 134 pieces → 1 run(s) | yes (continuous) | |
| 13_XL7089_PS_TC2.dgn | 30483 | RD_MK_LineSkipCenterNew | 100000 300000 | 0 | 18: 100000 100000 100000 100000 100000 100000 (first at 0) | yes | yes |
| XL7087_PS GR1.dgn | 1789 | AA_TM_Double | solid | | 3 pieces → 1 run(s) | yes (continuous) | |
| XL7087_PS GR1.dgn | 1852 | AA_TM_Double | solid | | 3 pieces → 1 run(s) | yes (continuous) | |
| XL7087_PS GR1.dgn | 1883 | RD_BR_Guardrail | 127.192 63.596 95.394 63.596 | 200.092 | 5: 86.091 127.192 95.394 127.192 95.394 (first at 0) | yes | yes |
| XL7087_PS GR1.dgn | 1892 | RD_BR_GuardrailNew | 286.183 63.596 | 254.385 | 18: 31.798 286.183 286.183 286.183 286.183 286.183 (first at 0) | yes | yes |
| XL7087_PS GR1.dgn | 1893 | RD_BR_Guardrail | 127.192 63.596 95.394 63.596 | 95.394 | 6: 31.798 95.394 127.192 95.394 127.192 95.394 (first at 0) | yes | yes |
| XL7087_PS GR1.dgn | 1946 | RD_BR_Guardrail | 127.192 63.596 95.394 63.596 | 95.394 | 5: 31.798 95.394 127.192 95.394 19.417 (first at 0) | yes | yes |
| XL7087_PS GR1.dgn | 1947 | RD_BR_GuardrailNew | 286.183 63.596 | 254.385 | 3: 31.798 286.183 178.408 (first at 0) | yes | yes |

26 samples: 21 dashed (19 with interior dashes) — interior dash lengths match in 19, full dash positions (phase) in 21; 5 drawn solid where MicroStation's stroked pieces form one continuous run.

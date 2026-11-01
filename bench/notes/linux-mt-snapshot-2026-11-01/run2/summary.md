## Medians (ms)

| kernel | toolchain | off | mt1 | mt2 | mt3 | mt4 | mt4 vs off | mt4 vs mt1 |
|---|---|---|---|---|---|---|---|---|
| antisym | blade | 54.77 | 52.64 | 27.17 | 21.29 | 15.52 | 3.53x | 3.39x |
|  | gfortran | 55.90 | 60.66 | 33.72 | 23.86 | 18.53 | 3.02x | 3.27x |
|  | ifx | 55.33 | 56.23 | 31.80 | 24.23 | 18.10 | 3.06x | 3.11x |
| band | blade | 2.94 | 4.08 | 3.91 | 3.15 | 2.80 | 1.05x | 1.46x |
|  | gfortran | 0.92 | 1.94 | 1.78 | 1.63 | 1.95 | 0.47x | 0.99x |
|  | ifx | 1.53 | 2.57 | 2.08 | 1.95 | 1.71 | 0.90x | 1.50x |
| comoment3 | blade | 15.45 | 15.39 | 8.78 | 7.09 | 5.49 | 2.82x | 2.80x |
|  | gfortran | 15.61 | 28.72 | 16.42 | 11.96 | 9.96 | 1.57x | 2.88x |
|  | ifx | 16.09 | 17.83 | 10.58 | 7.61 | 6.28 | 2.56x | 2.84x |
| dot | blade | 51.44 | 46.88 | 27.77 | 20.46 | 16.21 | 3.17x | 2.89x |
|  | gfortran | 35.36 | 34.16 | 22.92 | 16.93 | 14.39 | 2.46x | 2.37x |
|  | ifx | 37.33 | 43.89 | 26.92 | 20.00 | 17.08 | 2.19x | 2.57x |
| fusedchain | blade | 11.87 | 17.14 | 10.79 | 7.78 | 6.31 | 1.88x | 2.72x |
|  | gfortran | 12.76 | 13.68 | 8.51 | 7.40 | 6.81 | 1.87x | 2.01x |
|  | ifx | 13.67 | 15.73 | 9.00 | 8.02 | 6.93 | 1.97x | 2.27x |
| gram | blade | 91.22 | 96.40 | 53.71 | 35.42 | 28.62 | 3.19x | 3.37x |
|  | gfortran | 125.40 | 151.01 | 72.81 | 51.02 | 39.59 | 3.17x | 3.81x |
|  | ifx | 113.53 | 115.80 | 67.64 | 46.58 | 33.53 | 3.39x | 3.45x |
| haloadjoint | blade | 90.47 | 90.08 | 73.88 | 69.89 | 69.87 | 1.29x | 1.29x |
|  | gfortran | 21.02 | 22.44 | 12.37 | 10.79 | 10.32 | 2.04x | 2.17x |
|  | ifx | 29.35 | 30.93 | 19.06 | 15.03 | 11.88 | 2.47x | 2.60x |
| onepass | blade | 28.41 | 40.50 | 23.89 | 17.07 | 13.45 | 2.11x | 3.01x |
|  | gfortran | 32.42 | 38.19 | 21.15 | 16.10 | 13.63 | 2.38x | 2.80x |
|  | ifx | 32.54 | 36.90 | 21.83 | 17.69 | 14.79 | 2.20x | 2.50x |
| pairkernel | blade | 44.17 | 45.43 | 26.02 | 17.47 | 15.74 | 2.81x | 2.89x |
|  | gfortran | 19.69 | 20.26 | 11.97 | 9.04 | 7.29 | 2.70x | 2.78x |
|  | ifx | 32.03 | 31.63 | 17.57 | 12.29 | 10.04 | 3.19x | 3.15x |
| recur | blade | 26.44 | 27.55 | 17.39 | 16.03 | 12.06 | 2.19x | 2.28x |
|  | gfortran | 25.61 | 27.34 | 15.47 | 12.01 | 9.87 | 2.60x | 2.77x |
|  | ifx | 26.52 | 26.03 | 15.16 | 11.33 | 9.19 | 2.89x | 2.83x |
| saxpy | blade | 40.54 | 38.68 | 22.99 | 17.99 | 16.68 | 2.43x | 2.32x |
|  | gfortran | 28.99 | 28.98 | 20.97 | 14.45 | 12.54 | 2.31x | 2.31x |
|  | ifx | 34.54 | 38.22 | 23.19 | 17.59 | 14.01 | 2.47x | 2.73x |
| stencil | blade | 30.98 | 36.44 | 23.50 | 16.91 | 14.29 | 2.17x | 2.55x |
|  | gfortran | 29.42 | 33.87 | 17.47 | 14.22 | 11.49 | 2.56x | 2.95x |
|  | ifx | 28.63 | 33.11 | 16.33 | 13.27 | 10.69 | 2.68x | 3.10x |
| twofir | blade | 32.27 | 36.88 | 22.41 | 16.50 | 14.05 | 2.30x | 2.63x |
|  | gfortran | 27.39 | 27.92 | 15.74 | 12.06 | 10.35 | 2.65x | 2.70x |
|  | ifx | 33.19 | 34.11 | 21.02 | 14.79 | 12.57 | 2.64x | 2.71x |

## Blade vs Fortran at equal threads (Fortran time / Blade time; >1 = Blade faster)

| kernel | vs gfortran off | vs gfortran mt4 | vs ifx off | vs ifx mt4 | fastest at mt4 |
|---|---|---|---|---|---|
| antisym | 1.02 | 1.19 | 1.01 | 1.17 | blade |
| band | 0.31 | 0.70 | 0.52 | 0.61 | ifx |
| comoment3 | 1.01 | 1.81 | 1.04 | 1.14 | blade |
| dot | 0.69 | 0.89 | 0.73 | 1.05 | gfortran |
| fusedchain | 1.08 | 1.08 | 1.15 | 1.10 | blade |
| gram | 1.37 | 1.38 | 1.24 | 1.17 | blade |
| haloadjoint | 0.23 | 0.15 | 0.32 | 0.17 | gfortran |
| onepass | 1.14 | 1.01 | 1.15 | 1.10 | blade |
| pairkernel | 0.45 | 0.46 | 0.73 | 0.64 | gfortran |
| recur | 0.97 | 0.82 | 1.00 | 0.76 | ifx |
| saxpy | 0.72 | 0.75 | 0.85 | 0.84 | gfortran |
| stencil | 0.95 | 0.80 | 0.92 | 0.75 | ifx |
| twofir | 0.85 | 0.74 | 1.03 | 0.89 | gfortran |

## Checksum agreement (max relative spread over every arm) and errors

- antisym: checksum 6.3e-15, input_checksum 8.5e-16
- band: checksum 2.7e-15, input_checksum 3.3e-15
- comoment3: checksum 1.0e-14, input_checksum 6.3e-16
- dot: checksum 1.5e-14, input_checksum 1.6e-16
- fusedchain: checksum 7.6e-15, input_checksum 3.5e-16
- gram: checksum 3.6e-15, input_checksum 2.9e-16
- haloadjoint: checksum 2.0e-15, input_checksum 1.2e-16
- onepass: checksum 1.3e-14, input_checksum 3.7e-16
- pairkernel: checksum 2.3e-15, input_checksum 9.7e-16
- recur: checksum 8.5e-16, input_checksum 1.8e-15
- saxpy: checksum 1.0e-14, input_checksum 2.1e-15
- stencil: checksum 2.0e-15, input_checksum 2.9e-15
- twofir: checksum 7.1e-15, input_checksum 3.5e-16

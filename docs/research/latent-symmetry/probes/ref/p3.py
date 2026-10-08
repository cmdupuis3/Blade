import numpy as np, math
from scipy.linalg import expm
A=np.array([[0,0.5],[-0.5,0]])
def t6(A,s=3,deg=6):
    B=A/2**s; T=np.eye(len(A)); P=np.eye(len(A))
    for k in range(1,deg+1):
        P=P@B; T=T+P/math.factorial(k)
    for _ in range(s): T=T@T
    return T
E=t6(A)
print(repr(E)); print(E-expm(A))
def loss(A): e=t6(A); return 2*e[0,1]+e[1,1]
print("loss",repr(loss(A)))
h=1e-6
g=np.zeros((2,2))
for i in range(2):
  for j in range(2):
    D=np.zeros((2,2));D[i,j]=h; g[i,j]=(loss(A+D)-loss(A-D))/(2*h)
print(g)
# exact gradient of exact expm loss for comparison
def lossx(A): e=expm(A); return 2*e[0,1]+e[1,1]
for i in range(2):
  for j in range(2):
    D=np.zeros((2,2));D[i,j]=h; g[i,j]=(lossx(A+D)-lossx(A-D))/(2*h)
print(g)
# p3c action: A3 3x3, x
A3=np.array([[0.0,0.3,-0.2],[-0.3,0.0,0.4],[0.2,-0.4,0.1]])
x=np.array([1.0,-0.5,0.25])
y=expm(A3)@x
print("expmx",repr(y))
w=np.array([1.0,2.0,-1.0])
lossc=lambda A: w@(expm(A)@x)
print("lossc",repr(lossc(A3)))
G=np.zeros((3,3))
for i in range(3):
  for j in range(3):
    D=np.zeros((3,3));D[i,j]=h; G[i,j]=(lossc(A3+D)-lossc(A3-D))/(2*h)
print(G)

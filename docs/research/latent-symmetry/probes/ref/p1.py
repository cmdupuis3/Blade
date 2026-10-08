import numpy as np
X=np.array([[0.5,-1.0,0.25],[1.0,0.5,-0.5],[-0.75,0.25,1.0],[0.0,1.5,-0.25],[0.3,-0.2,0.8]])
Z=np.array([[1.0,0.0,-0.5],[0.25,0.75,0.5],[-0.5,1.0,0.0],[0.6,-0.4,0.2],[-1.0,0.3,0.7]])
L=np.array([[1.0,0.2,0.0],[0.1,0.9,0.3],[0.0,-0.2,1.1]])
k=lambda A,B:(1+A@B.T/3)**3
def mmd(L):
    Y=Z@L
    return (k(X,X).sum()+k(Y,Y).sum()-2*k(X,Y).sum())/25
print("Kxx_sum",repr(k(X,X).sum()))
print("v0",repr(mmd(L)))
h=1e-6
g=np.zeros((3,3))
for i in range(3):
  for j in range(3):
    E=np.zeros((3,3));E[i,j]=h
    g[i,j]=(mmd(L+E)-mmd(L-E))/(2*h)
print(g)
# Gaussian
def gmmd(L,s2=2.0):
    Y=Z@L
    def K(A,B):
        d=(A*A).sum(1)[:,None]+(B*B).sum(1)[None,:]-2*A@B.T
        return np.exp(-d/(2*s2))
    return (K(X,X).sum()+K(Y,Y).sum()-2*K(X,Y).sum())/25
print("gauss v0",repr(gmmd(L)))
for i in range(3):
  for j in range(3):
    E=np.zeros((3,3));E[i,j]=h
    g[i,j]=(gmmd(L+E)-gmmd(L-E))/(2*h)
print(g)
